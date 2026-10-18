using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TempMonitor;

// ===========================================================================
// Ziele für die eigenen RGB-Effekte: alles, was LEDs einzeln (oder zumindest farbig) kann
// ===========================================================================
interface IRgbTarget
{
    /// Dauerhafte Kennung (für "an/aus" in den Einstellungen)
    string Id { get; }
    string Name { get; }
    string Kind { get; }
    /// LED-Anzahl je Abschnitt (jeder Abschnitt bekommt den Effekt über seine eigene Länge)
    int[] Segments { get; }
    void Begin();
    bool Write(IReadOnlyList<Color[]> segments);
    void End();
}

// ---------------------------------------------------------------------------
// Mainboard: ASRock Polychrome USB
// ---------------------------------------------------------------------------
sealed class PolychromeTarget : IRgbTarget
{
    readonly PolychromeUsb _dev;
    public PolychromeTarget(PolychromeUsb dev) => _dev = dev;
    public string Id => "polychrome";
    public string Name => "Mainboard (ASRock Polychrome)";
    public string Kind => "Mainboard";
    public int[] Segments => _dev.Zones.Select(z => Math.Max(1, z.LedCount)).ToArray();
    public void Begin() => _dev.EnterDirect();
    public bool Write(IReadOnlyList<Color[]> s) => _dev.WriteFrame(s);
    public void End() => _dev.ExitDirect();
}

// ---------------------------------------------------------------------------
// OpenRGB: über dessen SDK-Server (Port 6742) alle Geräte, die OpenRGB kennt
// Protokoll-Version 1: Kopf "ORGB" + Geräte-ID + Paket-ID + Größe (je 4 Byte, little endian)
// ---------------------------------------------------------------------------
sealed class OpenRgbClient : IDisposable
{
    const uint ReqCount = 0, ReqData = 1, ReqVersion = 40, SetName = 50, UpdateLeds = 1050, SetCustomMode = 1100;

    TcpClient? _tcp;
    NetworkStream? _net;
    readonly object _gate = new();
    uint _proto;
    public List<OpenRgbDevice> Devices { get; } = new();
    public bool Connected => _tcp?.Connected == true;
    public string Status { get; private set; } = "nicht verbunden";

    public sealed class OpenRgbDevice
    {
        public uint Index;
        public string Name = "";
        public int Type;
        public List<(string Name, int Leds)> Zones = new();
        public int TotalLeds;
    }

    public bool Connect()
    {
        try
        {
            Dispose();
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 2000, SendTimeout = 2000 };
            var task = tcp.ConnectAsync("127.0.0.1", 6742);
            if (!task.Wait(600) || !tcp.Connected) { tcp.Dispose(); Status = "OpenRGB läuft nicht (oder SDK-Server aus)"; return false; }
            _tcp = tcp;
            _net = tcp.GetStream();
            Send(0, SetName, Encoding.ASCII.GetBytes("Toast Temperatur\0"));
            Send(0, ReqVersion, BitConverter.GetBytes(1u));
            var v = Receive(ReqVersion, 1500);
            _proto = v != null && v.Length >= 4 ? Math.Min(1u, BitConverter.ToUInt32(v, 0)) : 0;
            Send(0, ReqCount, Array.Empty<byte>());
            var c = Receive(ReqCount, 2000) ?? throw new IOException("keine Antwort");
            uint count = BitConverter.ToUInt32(c, 0);
            Devices.Clear();
            for (uint i = 0; i < count; i++)
            {
                Send(i, ReqData, _proto >= 1 ? BitConverter.GetBytes(_proto) : Array.Empty<byte>());
                var d = Receive(ReqData, 4000);
                if (d == null) continue;
                var dev = Parse(d, i);
                if (dev != null && dev.TotalLeds > 0) Devices.Add(dev);
            }
            Status = $"verbunden · {Devices.Count} Geräte";
            RgbSettings.Log($"OpenRGB: Protokoll {_proto}, Geräte: " + string.Join(", ", Devices.Select(x => $"{x.Name} ({x.TotalLeds} LEDs)")));
            return true;
        }
        catch (Exception ex)
        {
            Status = "Fehler: " + ex.Message;
            RgbSettings.Log("OpenRGB: " + ex.Message);
            Dispose();
            return false;
        }
    }

    void Send(uint dev, uint id, byte[] payload)
    {
        lock (_gate)
        {
            var net = _net ?? throw new IOException("nicht verbunden");
            var h = new byte[16];
            Encoding.ASCII.GetBytes("ORGB").CopyTo(h, 0);
            BitConverter.GetBytes(dev).CopyTo(h, 4);
            BitConverter.GetBytes(id).CopyTo(h, 8);
            BitConverter.GetBytes((uint)payload.Length).CopyTo(h, 12);
            net.Write(h, 0, 16);
            if (payload.Length > 0) net.Write(payload, 0, payload.Length);
        }
    }

    /// Antwort mit dieser Paket-ID lesen; andere Pakete (z. B. Geräteliste geändert) überspringen
    byte[]? Receive(uint id, int timeoutMs)
    {
        var net = _net!;
        var until = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < until)
        {
            net.ReadTimeout = Math.Max(100, (int)(until - DateTime.Now).TotalMilliseconds);
            var h = ReadExact(net, 16);
            if (h == null) return null;
            if (Encoding.ASCII.GetString(h, 0, 4) != "ORGB") throw new IOException("ungültige Antwort");
            uint pid = BitConverter.ToUInt32(h, 8), size = BitConverter.ToUInt32(h, 12);
            var data = size > 0 ? ReadExact(net, (int)size) : Array.Empty<byte>();
            if (pid == id) return data;
        }
        return null;
    }

    static byte[]? ReadExact(NetworkStream s, int n)
    {
        var b = new byte[n];
        int got = 0;
        try
        {
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) return null;
                got += r;
            }
        }
        catch (IOException) { return null; }
        return b;
    }

    OpenRgbDevice? Parse(byte[] d, uint index)
    {
        try
        {
            int p = 4;   // data_size
            int I32() { int v = BitConverter.ToInt32(d, p); p += 4; return v; }
            ushort U16() { ushort v = BitConverter.ToUInt16(d, p); p += 2; return v; }
            string Str() { int len = U16(); string s = len > 0 ? Encoding.UTF8.GetString(d, p, Math.Max(0, len - 1)) : ""; p += len; return s; }

            var dev = new OpenRgbDevice { Index = index };
            dev.Type = I32();
            dev.Name = Str();
            if (_proto >= 1) Str();          // vendor
            Str(); Str(); Str(); Str();      // description, version, serial, location
            int modes = U16();
            I32();                           // active_mode
            for (int m = 0; m < modes; m++)
            {
                Str();
                p += 4 * 9;                  // value, flags, speed min/max, colors min/max, speed, direction, color_mode
                int nc = U16();
                p += 4 * nc;
            }
            int zones = U16();
            for (int z = 0; z < zones; z++)
            {
                string zn = Str();
                I32(); I32(); I32();         // type, leds_min, leds_max
                int cnt = I32();
                int matrix = U16();
                p += matrix;
                dev.Zones.Add((zn, cnt));
            }
            dev.TotalLeds = U16();
            return dev;
        }
        catch { return null; }
    }

    public void CustomMode(OpenRgbDevice dev)
    {
        try { Send(dev.Index, SetCustomMode, Array.Empty<byte>()); } catch { }
    }

    public bool Write(OpenRgbDevice dev, Color[] colors)
    {
        try
        {
            int n = dev.TotalLeds;
            var buf = new byte[4 + 2 + 4 * n];
            BitConverter.GetBytes((uint)buf.Length).CopyTo(buf, 0);
            BitConverter.GetBytes((ushort)n).CopyTo(buf, 4);
            for (int i = 0; i < n; i++)
            {
                var c = i < colors.Length ? colors[i] : Color.Black;
                buf[6 + i * 4] = c.R; buf[7 + i * 4] = c.G; buf[8 + i * 4] = c.B;
            }
            Send(dev.Index, UpdateLeds, buf);
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        try { _net?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _net = null;
        _tcp = null;
    }
}

sealed class OpenRgbTarget : IRgbTarget
{
    readonly OpenRgbClient _client;
    readonly OpenRgbClient.OpenRgbDevice _dev;
    public OpenRgbTarget(OpenRgbClient client, OpenRgbClient.OpenRgbDevice dev) { _client = client; _dev = dev; }
    public string Id => "openrgb:" + _dev.Name;
    public string Name => _dev.Name;
    public string Kind => "OpenRGB";
    public int[] Segments
    {
        get
        {
            var z = _dev.Zones.Where(x => x.Leds > 0).Select(x => x.Leds).ToArray();
            return z.Length > 0 ? z : new[] { _dev.TotalLeds };
        }
    }
    public void Begin() => _client.CustomMode(_dev);
    public bool Write(IReadOnlyList<Color[]> s) => _client.Connected && _client.Write(_dev, s.SelectMany(x => x).ToArray());
    public void End() { }
}

// ---------------------------------------------------------------------------
// XMG Oasis: Pumpenkopf- und Lüfter-LEDs (über Bluetooth – nur bei Änderung, höchstens 5× pro Sekunde)
// ---------------------------------------------------------------------------
sealed class OasisLedTarget : IRgbTarget
{
    readonly OasisLink _link;
    readonly Color?[] _last = new Color?[2];
    DateTime _lastSend = DateTime.MinValue;
    public OasisLedTarget(OasisLink link) => _link = link;
    public string Id => "oasis";
    public string Name => "XMG Oasis (Pumpenkopf + Lüfter)";
    public string Kind => "Oasis";
    public int[] Segments => new[] { 1, 1 };
    public void Begin() { _last[0] = _last[1] = null; }
    public bool Write(IReadOnlyList<Color[]> s)
    {
        if (!_link.Connected) return true;
        if ((DateTime.Now - _lastSend).TotalMilliseconds < 200) return true;
        for (int i = 0; i < 2 && i < s.Count; i++)
        {
            var c = s[i].Length > 0 ? s[i][0] : Color.Black;
            if (_last[i] is Color l && Math.Abs(l.R - c.R) + Math.Abs(l.G - c.G) + Math.Abs(l.B - c.B) < 9) continue;
            _link.SetLed(i == 0 ? 0x1E : 0x33, c);
            _last[i] = c;
            _lastSend = DateTime.Now;
        }
        return true;
    }
    public void End() { }
}

// ---------------------------------------------------------------------------
// Laptop-Tastatur mit ITE-829x-Controller (048d:8910, z. B. XMG/TUXEDO mit Einzeltasten-RGB)
// Feature-Bericht CC <cmd> <d0..d3>: 0x09 = Helligkeit (0–10), 0x01 = Taste (id = Zeile<<5 | Spalte) R G B
// ---------------------------------------------------------------------------
sealed class IteKeyboardTarget : IRgbTarget, IDisposable
{
    const int Rows = 6, Cols = 20;
    readonly SafeFileHandle _h;
    readonly int _len;
    readonly Color[] _last = new Color[Rows * Cols];
    bool _first = true;

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);

    IteKeyboardTarget(SafeFileHandle h, int len) { _h = h; _len = len; }

    public static IteKeyboardTarget? Probe()
    {
        try
        {
            var r = Hid.OpenFeature(0x048D, new ushort[] { 0x8910 }, 6);
            if (r == null) return null;
            RgbSettings.Log($"Tastatur ITE 829x gefunden ({r.Value.Path})");
            return new IteKeyboardTarget(r.Value.Handle, r.Value.FeatureLength);
        }
        catch (Exception ex) { RgbSettings.Log("Tastatur: " + ex.Message); return null; }
    }

    bool SendCmd(byte cmd, byte d0, byte d1, byte d2, byte d3)
    {
        var b = new byte[Math.Max(6, _len)];
        b[0] = 0xCC; b[1] = cmd; b[2] = d0; b[3] = d1; b[4] = d2; b[5] = d3;
        return HidD_SetFeature(_h, b, b.Length);
    }

    public string Id => "ite829x";
    public string Name => "Laptop-Tastatur (Einzeltasten-RGB)";
    public string Kind => "Tastatur";
    public int[] Segments => new[] { Rows * Cols };
    public void Begin() { SendCmd(0x09, 10, 0x02, 0, 0); _first = true; }

    public bool Write(IReadOnlyList<Color[]> s)
    {
        var cols = s.Count > 0 ? s[0] : Array.Empty<Color>();
        for (int i = 0; i < Rows * Cols && i < cols.Length; i++)
        {
            var c = cols[i];
            var l = _last[i];
            if (!_first && Math.Abs(l.R - c.R) + Math.Abs(l.G - c.G) + Math.Abs(l.B - c.B) < 6) continue;
            byte id = (byte)(((i / Cols) & 0x07) << 5 | ((i % Cols) & 0x1F));
            if (!SendCmd(0x01, id, c.R, c.G, c.B)) return false;
            _last[i] = c;
        }
        _first = false;
        return true;
    }

    public void End() { }
    public void Dispose() => _h.Dispose();
}
