using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TempMonitor;

// ===========================================================================
// RGB-Beleuchtung: ASRock Polychrome USB (Mainboards ab B550/X570, Controller 26CE:01A2)
// Eigene Umsetzung des Protokolls (öffentlich dokumentiert durch das OpenRGB-Projekt):
//   Berichte zu 64 Byte (+ Report-ID 0). Byte 1 = Befehl, Byte 3 = Zone/Konfig
//   0x14 Konfig lesen (0x01 vorhandene Zonen, 0x02 LED-Anzahl, 0x03 R/G-Tausch)
//   0x15 Konfig schreiben · 0x10 Zone setzen (Modus, G, R, B, Tempo) · 0x12 im Board speichern
// ===========================================================================
static class Hid
{
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HiddAttributes a);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    struct HiddAttributes { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    public sealed class Device : IDisposable
    {
        public required FileStream Stream { get; init; }
        public required int OutLength { get; init; }
        public required int InLength { get; init; }
        public required string Path { get; init; }
        public void Dispose() { try { Stream.Dispose(); } catch { } }
    }

    /// HID-Gerät mit Feature-Berichten (z. B. Tastatur-Beleuchtung)
    public static (SafeFileHandle Handle, int FeatureLength, string Path)? OpenFeature(ushort vid, ushort[] pids, int minFeature)
    {
        foreach (var path in Paths(vid))
        {
            var h = CreateFile(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var attr = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
            if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != vid || !pids.Contains(attr.ProductID)) { h.Dispose(); continue; }
            if (!HidD_GetPreparsedData(h, out var pre)) { h.Dispose(); continue; }
            HidP_GetCaps(pre, out var caps);
            HidD_FreePreparsedData(pre);
            if (caps.FeatureReportByteLength < minFeature) { h.Dispose(); continue; }
            return (h, caps.FeatureReportByteLength, path);
        }
        return null;
    }

    /// HID-Gerät nach Kennung und Usage-Page öffnen (für Geräte mit mehreren Schnittstellen)
    public static (SafeFileHandle Handle, int FeatureLength, int OutLength, string Path)? OpenUsage(ushort vid, ushort pid, ushort usagePage)
    {
        foreach (var path in Paths(vid))
        {
            var h = CreateFile(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var attr = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
            if (!HidD_GetAttributes(h, ref attr) || attr.ProductID != pid || !HidD_GetPreparsedData(h, out var pre)) { h.Dispose(); continue; }
            HidP_GetCaps(pre, out var caps);
            HidD_FreePreparsedData(pre);
            if (caps.UsagePage != usagePage) { h.Dispose(); continue; }
            return (h, caps.FeatureReportByteLength, caps.OutputReportByteLength, path);
        }
        return null;
    }

    /// Diagnose: alle HID-Geräte mit Kennung und Berichtsgrößen (nur lesen, ohne Zugriffsrechte)
    public static List<string> DescribeAll()
    {
        var res = new List<string>();
        foreach (var path in Paths(0))
        {
            try
            {
                using var h = CreateFile(path, 0, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                var attr = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                if (!HidD_GetAttributes(h, ref attr)) continue;
                string caps = "";
                if (HidD_GetPreparsedData(h, out var pre))
                {
                    HidP_GetCaps(pre, out var c);
                    HidD_FreePreparsedData(pre);
                    caps = $" Usage {c.UsagePage:X4}:{c.Usage:X4} In {c.InputReportByteLength} Out {c.OutputReportByteLength} Feature {c.FeatureReportByteLength}";
                }
                res.Add($"{attr.VendorID:X4}:{attr.ProductID:X4} v{attr.VersionNumber:X4}{caps}");
            }
            catch { }
        }
        return res.Distinct().OrderBy(x => x).ToList();
    }

    static List<string> Paths(ushort vid)
    {
        var list = new List<string>();
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try
        {
            for (int i = 0; ; i++)
            {
                var ifd = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifd, buf, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    if (vid == 0 || path.Contains($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase)) list.Add(path);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    /// Erstes HID-Gerät mit dieser Kennung, das 64-Byte-Berichte senden kann
    public static Device? Open(ushort vid, ushort[] pids)
    {
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);   // PRESENT | DEVICEINTERFACE
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
        try
        {
            for (int i = 0; ; i++)
            {
                var ifd = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(size);
                string path;
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifd, buf, size, out _, IntPtr.Zero)) continue;
                    path = Marshal.PtrToStringUni(buf + 4) ?? "";
                }
                finally { Marshal.FreeHGlobal(buf); }
                if (!path.Contains($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase)) continue;

                // GENERIC_READ|WRITE, Teilen lesen/schreiben, OPEN_EXISTING, OVERLAPPED
                var h = CreateFile(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (h.IsInvalid) continue;
                var attr = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != vid || !pids.Contains(attr.ProductID)) { h.Dispose(); continue; }
                if (!HidD_GetPreparsedData(h, out var pre)) { h.Dispose(); continue; }
                HidP_GetCaps(pre, out var caps);
                HidD_FreePreparsedData(pre);
                if (caps.OutputReportByteLength < 65) { h.Dispose(); continue; }
                return new Device
                {
                    Stream = new FileStream(h, FileAccess.ReadWrite, 1, true),
                    OutLength = caps.OutputReportByteLength,
                    InLength = Math.Max(65, (int)caps.InputReportByteLength),
                    Path = path,
                };
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }
}

enum RgbZoneKind { Fixed, Addressable }

sealed class RgbZone
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public RgbZoneKind Kind { get; init; }
    public int LedCount { get; set; }
    public bool RgSwap { get; set; }
}

sealed class PolychromeUsb : IDisposable
{
    static readonly string[] ZoneNames =
    {
        "RGB-Anschluss 1", "RGB-Anschluss 2", "ARGB-Anschluss 1", "ARGB-Anschluss 2",
        "Chipsatz-Beleuchtung", "I/O-Abdeckung", "Platinen-Beleuchtung", "ARGB-Anschluss 3 / Audio",
    };

    readonly Hid.Device _dev;
    readonly object _gate = new();
    readonly byte[] _ledCfg = new byte[8];
    public List<RgbZone> Zones { get; } = new();
    public string? LastError { get; private set; }

    PolychromeUsb(Hid.Device dev) => _dev = dev;

    public static PolychromeUsb? Probe()
    {
        try
        {
            var dev = Hid.Open(0x26CE, new ushort[] { 0x01A2, 0x01A6 });
            if (dev == null) return null;
            var p = new PolychromeUsb(dev);
            if (!p.ReadConfig()) { dev.Dispose(); return null; }
            RgbSettings.Log($"Polychrome USB gefunden ({dev.Path}) · Zonen: " +
                            string.Join(", ", p.Zones.Select(z => $"{z.Name} ({z.LedCount} LEDs)")));
            return p;
        }
        catch (Exception ex) { RgbSettings.Log("Suche fehlgeschlagen: " + ex.Message); return null; }
    }

    /// Bericht senden und Antwort lesen (ohne führende Report-ID)
    byte[]? Transfer(byte[] data, int timeoutMs = 600)
    {
        lock (_gate)
        {
            try
            {
                var buf = new byte[_dev.OutLength];
                Array.Copy(data, 0, buf, 1, Math.Min(data.Length, buf.Length - 1));   // buf[0] = Report-ID 0
                _dev.Stream.Write(buf, 0, buf.Length);
                var inBuf = new byte[_dev.InLength];
                using var cts = new CancellationTokenSource(timeoutMs);
                int n = _dev.Stream.ReadAsync(inBuf, 0, inBuf.Length, cts.Token).GetAwaiter().GetResult();
                LastError = null;
                return n > 1 ? inBuf[1..n] : null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }
    }

    // data[i] entspricht hier Byte i des 64-Byte-Berichts (ohne Report-ID)
    static byte[] Msg(byte cmd, byte arg, params (int Index, byte Value)[] fields)
    {
        var b = new byte[64];
        b[0] = cmd;
        b[2] = arg;
        foreach (var (i, v) in fields) b[i] = v;
        return b;
    }

    bool ReadConfig()
    {
        var leds = Transfer(Msg(0x14, 0x02));
        var swap = Transfer(Msg(0x14, 0x03));
        var avail = Transfer(Msg(0x14, 0x01));
        // Antwort (ohne Report-ID): Nutzdaten ab Byte 4
        if (leds == null || avail == null || leds.Length < 12) return false;
        Array.Copy(leds, 4, _ledCfg, 0, 8);
        byte swapBits = swap != null && swap.Length > 4 ? swap[4] : (byte)0;
        byte availBits = avail.Length > 4 ? avail[4] : (byte)0;
        RgbSettings.Log($"Konfig: LEDs {Convert.ToHexString(_ledCfg)} · Tausch {swapBits:X2} · vorhanden {availBits:X2}");
        for (int z = 0; z < 8; z++)
        {
            if (_ledCfg[z] == 0x1E || ((availBits >> z) & 1) == 0) continue;
            Zones.Add(new RgbZone
            {
                Index = z,
                Name = ZoneNames[z],
                Kind = z is 2 or 3 or 7 ? RgbZoneKind.Addressable : RgbZoneKind.Fixed,
                LedCount = _ledCfg[z],
                RgSwap = ((swapBits >> z) & 1) != 0,
            });
        }
        return Zones.Count > 0;
    }

    /// Vom Nutzer gesetzt: Rot/Grün am Mainboard tauschen
    public bool SwapRgFix { get; set; }

    /// Farbreihenfolge neu lesen (ein anderes Programm wie OpenRGB kann sie geändert haben)
    public void RefreshConfig()
    {
        var swap = Transfer(Msg(0x14, 0x03));
        if (swap == null || swap.Length <= 4) return;
        byte bits = swap[4];
        foreach (var z in Zones) z.RgSwap = ((bits >> z.Index) & 1) != 0;
        RgbSettings.Log($"Konfig neu gelesen: Tausch {bits:X2}");
    }

    /// Effekt einer Zone setzen. speed 0–100 (100 = schnell)
    public bool SetZone(RgbZone z, byte mode, Color c, int speed)
    {
        byte sp = (byte)Math.Clamp(255 - speed * 255 / 100, 0, 255);
        if (SwapRgFix) c = Color.FromArgb(c.G, c.R, c.B);
        byte first = z.RgSwap ? c.R : c.G, second = z.RgSwap ? c.G : c.R;
        return Transfer(Msg(0x10, (byte)z.Index, (3, mode), (4, first), (5, second), (6, c.B), (7, sp), (8, 0xFF))) != null;
    }

    /// LED-Anzahl eines ARGB-Anschlusses (1–100)
    public bool SetLedCount(RgbZone z, int count)
    {
        _ledCfg[z.Index] = (byte)Math.Clamp(count, 1, 100);
        z.LedCount = _ledCfg[z.Index];
        var fields = Enumerable.Range(0, 8).Select(i => (3 + i, _ledCfg[i])).ToArray();
        return Transfer(Msg(0x15, 0x02, fields)) != null;
    }

    // ----- Direkt-Modus: jede LED einzeln (für die eigenen Effekte von Toast Temperatur) -----
    public bool Direct { get; private set; }

    public void EnterDirect()
    {
        Transfer(Msg(0x15, 0x03, (3, 0)));                  // R/G-Tausch aus (sonst flackert es bei jedem Bild)
        foreach (var z in Zones) Transfer(Msg(0x10, (byte)z.Index, (3, 0x0F), (7, 0xE0), (8, 0xFF)));
        Direct = true;
    }

    public void ExitDirect()
    {
        if (!Direct) return;
        byte bits = 0;
        foreach (var z in Zones) if (z.RgSwap) bits |= (byte)(1 << z.Index);
        Transfer(Msg(0x15, 0x03, (3, bits)));
        Direct = false;
    }

    /// Ein Bild schicken: Farben je Zone (Reihenfolge wie Zones), lückenlos hintereinander
    public bool WriteFrame(IReadOnlyList<Color[]> perZone)
    {
        var bytes = new List<byte>(1024);
        int capacity = 0;
        for (int i = 0; i < Zones.Count; i++)
        {
            var z = Zones[i];
            var cols = i < perZone.Count ? perZone[i] : Array.Empty<Color>();
            for (int l = 0; l < z.LedCount; l++)
            {
                var c = l < cols.Length ? cols[l] : Color.Black;
                bool sw = z.RgSwap ^ SwapRgFix;
                bytes.Add(sw ? c.R : c.G);
                bytes.Add(sw ? c.G : c.R);
                bytes.Add(c.B);
            }
            capacity += z.Kind == RgbZoneKind.Addressable ? 100 : z.LedCount;
        }
        int total = (capacity + 8) * 3;
        while (bytes.Count < total) bytes.Add(0);

        int pos = 0;
        bool first = true;
        while (pos < bytes.Count)
        {
            var b = new byte[64];
            b[0] = 0x10;
            b[2] = 0xFF;
            b[3] = first ? (byte)0xE3 : (byte)0xE4;
            if (first) b[6] = 0xFF;
            b[63] = 0x65;
            int offset = first ? 8 : 4, cap = first ? 54 : 57;
            for (int k = 0; k < cap && pos < bytes.Count; k++) b[offset + k] = bytes[pos++];
            if (Transfer(b, 120) == null && LastError != null) return false;
            first = false;
        }
        return true;
    }

    /// Im Mainboard speichern – bleibt dann auch ohne Toast Temperatur und nach dem Neustart
    public bool Commit() => Transfer(Msg(0x12, 0x00)) != null;

    public void Dispose() => _dev.Dispose();
}

// ---------------------------------------------------------------------------
// Einstellungen (%AppData%\TempMonitor\rgb.json)
// ---------------------------------------------------------------------------
sealed class RgbZoneSettings
{
    public int Mode { get; set; } = 1;          // Hardware-Modus, TempMode = Temperaturfarbe
    public int Color { get; set; } = unchecked((int)0xFF3B82F6);
    public int Speed { get; set; } = 50;
}

sealed class RgbSettings
{
    public const int TempMode = 100;
    public bool Sync { get; set; } = true;
    public string TempSource { get; set; } = "Max";   // CPU, GPU, Hotspot, Max
    public Dictionary<int, RgbZoneSettings> Zones { get; set; } = new();
    public bool ApplyOnStart { get; set; }
    /// Geräte, auf denen die eigenen Effekte NICHT laufen sollen
    public List<string> DisabledTargets { get; set; } = new();
    /// OpenRGB automatisch mitstarten: null = noch nicht gefragt
    public bool? OpenRgbAuto { get; set; }
    /// Geräte, bei denen Rot und Grün vertauscht ankommen (Häkchen in der Geräteliste)
    public List<string> SwapRG { get; set; } = new();
    /// Eigene Effekte, aber das Mainboard läuft mit seinen eigenen (stabilen) Effekten
    public bool BoardHardware { get; set; } = true;
    public bool BoardFollowMigrated { get; set; } = true;
    /// Mainboard-Anschlüsse ohne Beleuchtung: werden bei eigenen Effekten nicht angesprochen (weniger Befehle = weniger Blitze)
    public List<int> BoardZonesOff { get; set; } = new();
    /// Farbbefehle pro Sekunde ans Mainboard bei eigenen Effekten (mehr = weicher, aber mehr Blitz-Gefahr bei manchen Controllern)
    public int BoardFps { get; set; } = 4;
    public bool BoardWearMigrated { get; set; } = true;
    /// Mainboard: jede LED einzeln (Direktmodus, experimentell) statt einer Farbe je Zone
    public bool BoardPerLed { get; set; }

    /// true = eigene Effekte von Toast Temperatur (jede LED einzeln), false = Effekte des Mainboards
    public bool Custom { get; set; } = true;
    public CustomEffect Effect { get; set; } = CustomEffect.Presets[0].Effect.Clone();

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");
    static readonly string FilePath = Path.Combine(Dir, "rgb.json");

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "rgb.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public static RgbSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<RgbSettings>(json) ?? new();
                // Einmalig: Mainboard standardmäßig mit eigenen (stabilen) Effekten, die dem eigenen Effekt folgen
                if (!json.Contains("\"BoardFollowMigrated\"")) { s.BoardHardware = true; s.BoardFollowMigrated = true; }
                // Schutz: ASRock speichert jede Einstellung im Controller-Speicher -> Software-Dauersteuerung nutzt ihn ab
                if (!json.Contains("\"BoardWearMigrated\"")) { s.BoardHardware = true; s.BoardPerLed = false; s.BoardWearMigrated = true; if (s.BoardFps > 4) s.BoardFps = 4; }
                return s;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public RgbZoneSettings For(int zone)
    {
        if (!Zones.TryGetValue(zone, out var z)) { z = new(); Zones[zone] = z; }
        return z;
    }
}

// ---------------------------------------------------------------------------
// Steuerung: Einstellungen anwenden, Temperaturfarbe nachführen
// ---------------------------------------------------------------------------
sealed class RgbService
{
    public PolychromeUsb? Device { get; }
    public List<IRgbTarget> Targets { get; } = new();
    public OpenRgbClient OpenRgb { get; } = new();
    IteKeyboardTarget? _keyboard;
    /// Geräte haben sich geändert (OpenRGB verbunden, Oasis gefunden …)
    public event Action? TargetsChanged;
    public RgbSettings Settings { get; }
    readonly Func<KeySensors?> _keys;
    readonly Dictionary<int, Color> _lastTempColor = new();
    readonly Dictionary<int, DateTime> _lastTempWrite = new();

    public RgbService(PolychromeUsb? dev, Func<KeySensors?> keys)
    {
        Device = dev;
        if (dev != null) Targets.Add(new PolychromeTarget(dev, () => Settings?.BoardPerLed == true, z => Settings?.BoardZonesOff.Contains(z) != true, () => Settings?.BoardFps ?? 10));
        _keyboard = IteKeyboardTarget.Probe();
        Task.Run(Diagnose);
        if (_keyboard != null) Targets.Add(_keyboard);
        if (Ite8291Keyboard.Probe() is Ite8291Keyboard k8291) Targets.Add(k8291);
        if (Ite8291Lightbar.Probe() is Ite8291Lightbar lb) Targets.Add(lb);
        _keys = keys;
        Settings = RgbSettings.Load();
        if (dev != null) dev.SwapRgFix = Settings.SwapRG.Contains("polychrome");
        Engine = new EffectEngine(this);
        if (dev == null) Settings.Custom = true;   // ohne Mainboard-Controller gibt es nur eigene Effekte
        if (Settings.Custom) Engine.Start();
        if (!Settings.Custom ? Settings.ApplyOnStart : BoardNative) Task.Run(() => { if (Settings.Custom) ApplyBoardFromCustom(); else ApplyAll(); });
        new Thread(Discover) { IsBackground = true, Name = "RGB-Suche" }.Start();
        if (Settings.OpenRgbAuto == true && Settings.Custom) StartOpenRgb();
    }

    public EffectEngine Engine { get; }

    /// Einmal je Start: welche Beleuchtungs-Hardware gibt es? (nur lesen) -> rgb.log
    static void Diagnose()
    {
        try
        {
            var all = Hid.DescribeAll();
            RgbSettings.Log($"HID-Geräte ({all.Count}): " + string.Join(" | ", all));
            if (UniwillEc.Current is UniwillEc ec)
            {
                var regs = new ushort[] { 0x0740, 0x0766, 0x0767, 0x078C, 0x1801, 0x1802, 0x1803, 0x1805, 0x1808 };
                RgbSettings.Log("Laptop-EC: " + string.Join(" ", regs.Select(r => $"{r:X4}={(ec.Read(r) is byte b ? b.ToString("X2") : "--")}")));
            }
        }
        catch (Exception ex) { RgbSettings.Log("Diagnose: " + ex.Message); }
    }

    /// Im Hintergrund: OpenRGB und Oasis suchen (alle 30 s, falls sie später gestartet werden)
    void Discover()
    {
        while (true)
        {
            DiscoverOnce();
            // alle 30 s – oder sofort, wenn OpenRGB eine geänderte Geräteliste meldet
            for (int i = 0; i < 15 && !OpenRgb.NeedsRefresh; i++) Thread.Sleep(2000);
        }
    }

    public void RescanNow() => Task.Run(DiscoverOnce);

    readonly object _discoverGate = new();

    void DiscoverOnce()
    {
        lock (_discoverGate)
        {
            try
            {
                bool changed = false;
                if (OpenRgb.NeedsRefresh) OpenRgb.Dispose();
                if (!OpenRgb.Connected && OpenRgb.Connect())
                {
                    lock (Targets)
                    {
                        Targets.RemoveAll(t => t is OpenRgbTarget);
                        foreach (var d in OpenRgb.Devices) Targets.Add(new OpenRgbTarget(OpenRgb, d));
                    }
                    changed = true;
                }
                else if (!OpenRgb.Connected && Targets.Any(t => t is OpenRgbTarget))
                {
                    lock (Targets) Targets.RemoveAll(t => t is OpenRgbTarget);
                    changed = true;
                }
                if (false && OasisLink.Instance is OasisLink ol && ol.Connected && !Targets.Any(t => t is OasisLedTarget))
                {
                    lock (Targets) Targets.Add(new OasisLedTarget(ol));
                    changed = true;
                }
                if (changed) TargetsChanged?.Invoke();
            }
            catch (Exception ex) { RgbSettings.Log("Suche: " + ex.Message); }
        }
    }

    public bool IsEnabled(IRgbTarget t)
    {
        if (Settings.DisabledTargets.Contains(t.Id)) return false;
        // ASRock-Mainboard nie über OpenRGB (dessen Direktmodus flackert dort) – Toast Temperatur steuert es selbst
        if (Device != null && IsBoardViaOpenRgb(t)) return false;
        if (BoardNative && Device != null && t is PolychromeTarget) return false;
        return true;
    }

    /// OpenRGB kennt das ASRock-Mainboard (dann läuft der Effekt dort über OpenRGB)
    public bool OpenRgbHasBoard
    {
        get
        {
            lock (Targets)
                return Targets.Any(x => x is OpenRgbTarget o && IsEnabledRaw(o)
                    && (o.Name.Contains("ASRock", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("Polychrome", StringComparison.OrdinalIgnoreCase)));
        }
    }

    static bool IsBoardViaOpenRgb(IRgbTarget t) =>
        t is OpenRgbTarget o && (o.Name.Contains("ASRock", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("Polychrome", StringComparison.OrdinalIgnoreCase));

    bool IsEnabledRaw(IRgbTarget t) => !Settings.DisabledTargets.Contains(t.Id);

    public float? Temperature() => _keys()?.Value(Settings.TempSource);

    /// Zwischen Mainboard-Effekten und eigenen Effekten umschalten
    public void SetCustom(bool on)
    {
        if (Device == null) on = true;
        Settings.Custom = on;
        Settings.Save();
        if (on)
        {
            Engine.Start();
            if (Settings.OpenRgbAuto == true) StartOpenRgb();
            if (BoardNative && Device != null) Task.Run(ApplyBoardFromCustom);
        }
        else Task.Run(() =>
        {
            Engine.Stop();
            // Mainboard-Effekte: OpenRGB darf nicht dazwischenfunken
            OpenRgbManager.Stop();
            Thread.Sleep(800);
            try { Device?.RefreshConfig(); } catch { }
            ApplyAll();
        });
    }

    /// Beim Beenden: eigene Effekte stoppen, das Mainboard übernimmt mit seinen Effekten
    /// OpenRGB starten und danach mehrmals nachsehen, bis sein SDK-Server bereit ist
    public void StartOpenRgb()
    {
        Task.Run(async () =>
        {
            OpenRgbManager.Start();
            foreach (int s in new[] { 4, 8, 15, 30 })
            {
                await Task.Delay(s * 1000);
                DiscoverOnce();
                if (OpenRgb.Connected)
                {
                    if (Device != null && (!Settings.Custom || BoardNative))
                    {
                        await Task.Delay(1500);
                        try { Device.RefreshConfig(); if (Settings.Custom) ApplyBoardFromCustom(); else ApplyAll(); } catch { }
                    }
                    break;
                }
            }
        });
    }

    public void Shutdown()
    {
        try { OpenRgbManager.Stop(); } catch { }
        if (!Engine.Running) return;
        Engine.Stop();
        try { ApplyAll(); } catch { }
    }

    public static readonly (string Name, byte Mode)[] Modes =
    {
        ("Aus", 0x00), ("Statisch", 0x01), ("Atmen", 0x02), ("Blitzen", 0x03), ("Farbwechsel", 0x04),
        ("Zufall", 0x05), ("Welle", 0x07), ("Feder", 0x08), ("Stapeln", 0x09), ("Füllen", 0x0A),
        ("Scannen", 0x0B), ("Neon", 0x0C), ("Wasser", 0x0D), ("Regenbogen", 0x0E),
        ("Temperatur als Farbe", RgbSettings.TempMode),
    };

    public void Apply(RgbZone z)
    {
        if (Device == null) return;
        var s = Settings.For(z.Index);
        if (s.Mode == RgbSettings.TempMode)
        {
            _lastTempColor.Remove(z.Index);
            TickTemperature(force: true);
            return;
        }
        if (!Device.SetZone(z, (byte)s.Mode, Color.FromArgb(s.Color), s.Speed))
            RgbSettings.Log($"{z.Name}: Senden fehlgeschlagen ({Device.LastError})");
    }

    /// Mainboard läuft mit seinem eingebauten Effekt: im Mischbetrieb immer – sonst automatisch, wenn der eigene
    /// Effekt genau dem eingebauten entspricht (dann butterweich, ohne die Blitze beim Farbwechsel per Software)
    public bool BoardNative => Settings.BoardHardware || EffectFitsBoard();

    bool EffectFitsBoard()
    {
        var fx = Settings.Effect;
        bool one = fx.Colors.Count <= 1;
        return fx.Type switch
        {
            EffectType.Breathing => one && fx.MinLevel == 0 && fx.FadeInMs == 0 && fx.HoldOnMs == 0 && fx.FadeOutMs == 0 && fx.HoldOffMs == 0,
            EffectType.RainbowWave or EffectType.ColorCycle => true,
            EffectType.Gradient => one,
            _ => false,
        };
    }

    (byte Mode, string Name) BoardMode() => Settings.Effect.Type switch
    {
        EffectType.Breathing => (0x02, "Atmen"),
        EffectType.ColorCycle => (0x04, "Farbwechsel"),
        EffectType.Sparkle => (0x05, "Zufall"),
        EffectType.Wave or EffectType.RainbowWave => (0x07, "Welle"),
        EffectType.Comet => (0x0B, "Scannen"),
        EffectType.Fire => (0x0C, "Neon"),
        EffectType.TempWave => ((byte)RgbSettings.TempMode, "Temperatur als Farbe"),
        _ => (0x01, "Statisch"),
    };

    public string BoardFollowText()
    {
        var fx = Settings.Effect;
        var c = fx.Colors.Count > 0 ? Color.FromArgb(fx.Colors[0]) : Color.FromArgb(0, 120, 255);
        return $"{BoardMode().Name}, erste Farbe (#{c.R:X2}{c.G:X2}{c.B:X2}), Tempo {fx.Speed} %";
    }

    /// Mischbetrieb: Mainboard-Effekt aus dem eigenen Effekt ableiten (Typ, erste Farbe × Helligkeit, Tempo)
    public void ApplyBoardFromCustom()
    {
        if (Device == null || !BoardNative) return;
        var fx = Settings.Effect;
        var c = fx.Colors.Count > 0 ? Color.FromArgb(fx.Colors[0]) : Color.FromArgb(0, 120, 255);
        float b = Math.Clamp(fx.Brightness, 5, 100) / 100f;
        c = Color.FromArgb((int)(c.R * b), (int)(c.G * b), (int)(c.B * b));
        var mode = BoardMode().Mode;
        foreach (var z in Device.Zones)
        {
            var s = Settings.For(z.Index);
            s.Mode = mode; s.Color = c.ToArgb(); s.Speed = Math.Clamp(fx.Speed, 0, 100);
        }
        Settings.Save();
        ApplyAll();
    }

    public void ApplyAll()
    {
        if (Device == null) return;
        foreach (var z in Device.Zones) Apply(z);
    }

    /// Farbe aus Temperatur: unter 45 °C blau, 60 grün, 75 gelb, ab 88 rot
    public static Color TempColor(float t)
    {
        (float T, Color C)[] stops =
        {
            (45, Color.FromArgb(40, 120, 255)), (60, Color.FromArgb(40, 220, 90)),
            (75, Color.FromArgb(255, 200, 0)), (88, Color.FromArgb(255, 30, 20)),
        };
        if (t <= stops[0].T) return stops[0].C;
        for (int i = 1; i < stops.Length; i++)
        {
            if (t > stops[i].T) continue;
            float f = (t - stops[i - 1].T) / (stops[i].T - stops[i - 1].T);
            var a = stops[i - 1].C; var b = stops[i].C;
            return Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
        }
        return stops[^1].C;
    }

    /// Jede Sekunde: Zonen im Temperatur-Modus nachführen (nur bei spürbarer Änderung senden)
    public void TickTemperature(bool force = false)
    {
        if ((Settings.Custom && !BoardNative) || Device == null) return;
        if (_keys()?.Value(Settings.TempSource) is not float t) return;
        var col = TempColor(t);
        foreach (var z in Device!.Zones)
        {
            if (Settings.For(z.Index).Mode != RgbSettings.TempMode) continue;
            // Schonend: der Controller speichert jede Änderung -> höchstens alle 30 s und nur bei deutlicher Farbänderung
            if (!force && _lastTempColor.TryGetValue(z.Index, out var last)
                && (Math.Abs(last.R - col.R) + Math.Abs(last.G - col.G) + Math.Abs(last.B - col.B) < 40
                    || (DateTime.Now - _lastTempWrite.GetValueOrDefault(z.Index)).TotalSeconds < 30)) continue;
            if (Device.SetZone(z, 0x01, col, 50)) { _lastTempColor[z.Index] = col; _lastTempWrite[z.Index] = DateTime.Now; }
        }
    }
}

// ---------------------------------------------------------------------------
// Reiter "RGB"
// ---------------------------------------------------------------------------
sealed class RgbView : FlowLayoutPanel
{
    readonly RgbService _svc;
    readonly List<Control> _full = new();
    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public RgbView(RgbService svc)
    {
        _svc = svc;
        AutoScroll = true;
        BackColor = Theme.Background;
        Padding = new Padding(S(12), S(6), S(12), S(12));
        Resize += (_, _) => { int w = DarkMode.FullWidth(this, S(16)); foreach (var c in _full) c.Width = w; };
        DarkMode.Use(this);
        DarkMode.NoHorizontalScroll(this);
        _svc.TargetsChanged += OnTargetsChanged;
        Build();
    }

    async void SetupOpenRgb()
    {
        var st = _svc.Settings;
        if (MessageBox.Show(FindForm(),
                "OpenRGB jetzt einrichten?\n\nToast Temperatur lädt OpenRGB (Open Source, GPL-2.0) einmalig von codeberg.org/OpenRGB herunter " +
                "und startet es künftig automatisch unsichtbar mit. Danach laufen die eigenen Effekte über OpenRGB.",
                "OpenRGB einrichten", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        using var progress = new ProgressForm("OpenRGB wird eingerichtet …");
        progress.Show(FindForm());
        bool ok = OpenRgbManager.Installed || await OpenRgbManager.InstallAsync(new Progress<string>(m => progress.Text = m));
        progress.Close();
        if (!ok)
        {
            MessageBox.Show(FindForm(), OpenRgbManager.Status + "\n\nAlternativ: OpenRGB von openrgb.org selbst installieren und den SDK-Server starten.",
                "OpenRGB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        st.OpenRgbAuto = true;
        st.Save();
        _svc.StartOpenRgb();
        RebuildSoon();
        MessageBox.Show(FindForm(), "OpenRGB ist eingerichtet und startet gerade. In ca. 10–30 Sekunden erscheinen die Geräte hier, " +
            "beim Mainboard steht dann „läuft gerade über OpenRGB“.", "OpenRGB", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void OnTargetsChanged()
    {
        try { if (IsHandleCreated && !IsDisposed) RebuildSoon(); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _svc.TargetsChanged -= OnTargetsChanged;
        base.Dispose(disposing);
    }

    void Add(Control c)
    {
        _full.Add(c);
        Controls.Add(c);
        SetFlowBreak(c, true);
        c.Width = DarkMode.FullWidth(this, S(16));
    }

    void Build()
    {
        var st = _svc.Settings;
        Add(new SectionHeader("RGB-Beleuchtung", Color.FromArgb(244, 114, 182)) { Height = S(40), Margin = new Padding(S(6), S(14), S(6), S(4)) });
        var top = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        if (_svc.Device != null)
        {
            top.Combo("Steuerung:", new[] { "Einfache Mainboard-Effekte (nur Farbe + Tempo, laufen auch ohne Toast Temperatur)", "Eigene Effekte (alle Einstellungen: Helligkeit, nicht ganz aus, Zeiten …)" },
                st.Custom ? 1 : 0, i => { _svc.SetCustom(i == 1); RebuildSoon(); });
            if (top.Controls.OfType<ComboBox>().LastOrDefault() is ComboBox modeBox) modeBox.Width = S(420);
        }

        // Geräte: auf welchen die eigenen Effekte laufen
        List<IRgbTarget> targets;
        lock (_svc.Targets) targets = _svc.Targets.ToList();
        top.Note(targets.Count == 0 ? "Noch keine RGB-Geräte gefunden." : "Gefundene Geräte – Haken = eigene Effekte laufen dort:", muted: targets.Count == 0);
        foreach (var t in targets)
        {
            var tt = t;
            int leds = t.Segments.Sum();
            string extra = _svc.Device != null && t is OpenRgbTarget o && (o.Name.Contains("ASRock") || o.Name.Contains("Polychrome"))
                ? "   ·   wird von Toast Temperatur direkt gesteuert" : "";
            top.Check($"{t.Name}   ·   {t.Kind}, {leds} LED{(leds == 1 ? "" : "s")}{extra}", !st.DisabledTargets.Contains(t.Id), v =>
            {
                if (v) st.DisabledTargets.Remove(tt.Id); else if (!st.DisabledTargets.Contains(tt.Id)) st.DisabledTargets.Add(tt.Id);
                st.Save();
            });
            var swapBox = top.Check("      ↳ Rot und Grün sind vertauscht (Haken = korrigieren)", st.SwapRG.Contains(t.Id), v =>
            {
                if (v) { if (!st.SwapRG.Contains(tt.Id)) st.SwapRG.Add(tt.Id); } else st.SwapRG.Remove(tt.Id);
                st.Save();
                if (tt is PolychromeTarget && _svc.Device != null)
                {
                    _svc.Device.SwapRgFix = v;
                    if (!st.Custom) Task.Run(_svc.ApplyAll);
                }
            });
            swapBox.ForeColor = Theme.Muted;
        }
        if (_svc.Device != null && st.OpenRgbAuto != true)
        {
            top.Note("Empfohlen fürs Mainboard: Mit OpenRGB laufen die eigenen Effekte ohne Flackern und jede LED einzeln. " +
                     "Toast Temperatur lädt OpenRGB (Open Source, ca. 20 MB) einmalig von der offiziellen Projektseite " +
                     "codeberg.org/OpenRGB und startet es danach automatisch unsichtbar mit – sonst musst du nichts tun.", muted: false);
            top.Buttons(("OpenRGB einrichten (empfohlen)", () => SetupOpenRgb()),
                        ("Nein danke", () => { st.OpenRgbAuto = false; st.Save(); RebuildSoon(); }));
        }
        else if (st.OpenRgbAuto == true)
        {
            top.Check("OpenRGB automatisch mitstarten", true, v => { st.OpenRgbAuto = v; st.Save(); if (!v) OpenRgbManager.Stop(); else _svc.StartOpenRgb(); });
        }
        var rivals = RgbRivals.Running();
        if (rivals.Count > 0)
            top.Note("⚠ Läuft gleichzeitig: " + string.Join(", ", rivals) + ". Diese Programme steuern die Beleuchtung auch und funken dazwischen " +
                     "(Flackern, wilde Farben). Für eigene Effekte bitte beenden und aus dem Autostart nehmen.", muted: false);
        top.Note("OpenRGB: " + _svc.OpenRgb.Status + ". Für Tastaturen, Mäuse, RAM, Grafikkarten u. v. m.: OpenRGB (openrgb.org) starten, " +
                 "im Reiter „SDK Server“ auf „Start Server“ klicken (Port 6742) – Toast Temperatur findet die Geräte dann automatisch " +
                 "und spielt seine Effekte auch dort ab.");
        if (_svc.Device != null)
            { }
        top.Buttons(("Geräte neu suchen", () => _svc.RescanNow()));
        if (_svc.Device != null)
            top.Note("Mit Hub am ARGB-Anschluss: Ein Verteiler-Hub schickt allen Geräten dasselbe Signal – als LED-Anzahl die des längsten Geräts " +
                     "eintragen (oder die Summe, wenn die Geräte hintereinander geschaltet sind).");
        Add(top);

        if (st.Custom && _svc.Device != null)
        {
            var bh = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
            bh.Check("Mainboard mit seinen eigenen Effekten laufen lassen (stabil, kein Flackern) – eigene Effekte nur auf den anderen Geräten",
                st.BoardHardware, v =>
                {
                    if (!v && MessageBox.Show(FindForm(),
                            "Achtung – Risiko für das Mainboard:\n\n" +
                            "Der RGB-Controller dieses ASRock-Mainboards speichert jede Farbänderung in seinem internen Speicher. " +
                            "Werden eigene Effekte per Software gesteuert, schreibt Toast Temperatur laufend neue Farben – das kann diesen " +
                            "Speicher mit der Zeit abnutzen und die Mainboard-Beleuchtung dauerhaft beschädigen. Außerdem blitzt es dabei.\n\n" +
                            "Empfohlen: Haken drin lassen (eingebaute Effekte, keine Abnutzung).\n\n" +
                            "Trotzdem per Software steuern? (auf eigene Gefahr, höchstens wenige Farbbefehle pro Sekunde)",
                            "Mainboard-Beleuchtung – bitte bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        RebuildSoon();
                        return;
                    }
                    st.BoardHardware = v; st.Save();
                    if (v) Task.Run(_svc.ApplyBoardFromCustom);
                    RebuildSoon();
                });
            bh.Note(st.BoardHardware
                ? "Das Mainboard folgt automatisch deinem Effekt so gut es kann: " + _svc.BoardFollowText() +
                  ". Helligkeit wirkt über die Farbe; „nicht ganz aus“ und die Atmen-Zeiten kann nur die Software (Maus, Tastatur …)."
                : "Ohne Haken bekommt auch das Mainboard die eigenen Effekte mit allen Einstellungen (eine Farbe je Anschluss, " +
                  "ca. 10× pro Sekunde). Bei manchen Hubs blitzt es dabei, wenn sich die Farbe ändert. Entspricht dein Effekt " +
                  "genau einem eingebauten (z. B. Atmen mit einer Farbe, ohne „nicht ganz aus“ und ohne eigene Zeiten), " +
                  "nimmt Toast Temperatur automatisch den eingebauten – der läuft ohne Blitze.");
            Add(bh);
        }
        if (st.Custom || _svc.Device == null) BuildCustom();
        if (!st.Custom && _svc.Device != null) BuildHardware();

        if (_svc.Device is PolychromeUsb board)
        {
            var info = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
            info.Note("Mainboard: " + string.Join(" · ", board.Zones.Select(z => z.Kind == RgbZoneKind.Addressable ? $"{z.Name} ({z.LedCount} LEDs)" : z.Name)));
            if (!st.BoardHardware) info.Numbers(("Farbbefehle pro Sekunde ans Mainboard (nur bei Software-Steuerung; wenig = schonender)", (float)Math.Clamp(st.BoardFps, 1, 5), 1, 5, "",
                (Action<float>)(v => { st.BoardFps = (int)v; st.Save(); })));
            info.Note("Benutzte Anschlüsse – nur wo wirklich Beleuchtung dranhängt (weniger Befehle ans Mainboard = weniger Blitze):", muted: false);
            foreach (var z in board.Zones)
            {
                var zz = z;
                info.Check(z.Name, !st.BoardZonesOff.Contains(z.Index), v =>
                {
                    if (v) st.BoardZonesOff.Remove(zz.Index); else if (!st.BoardZonesOff.Contains(zz.Index)) st.BoardZonesOff.Add(zz.Index);
                    st.Save();
                });
            }
            foreach (var z in board.Zones.Where(z => z.Kind == RgbZoneKind.Addressable))
            {
                var zz = z;
                info.Numbers(($"LEDs an {z.Name}", (float)z.LedCount, 1, 100, "", (Action<float>)(v => Debounce(() => board.SetLedCount(zz, (int)v)))));
            }
            Add(info);
        }
    }

    void BuildHardware()
    {
        var st = _svc.Settings;
        var hw = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        hw.Check("Alle Anschlüsse gleich einstellen", st.Sync, v => { st.Sync = v; st.Save(); RebuildSoon(); });
        string[] sources = { "CPU", "GPU", "Hotspot", "Max" };
        hw.Combo("„Temperatur als Farbe“ nach:", new[] { "CPU", "Grafikkarte", "GPU-Hot-Spot", "Höchster Wert von CPU und GPU" },
            Array.IndexOf(sources, st.TempSource), i => { st.TempSource = sources[i]; st.Save(); _svc.TickTemperature(force: true); });
        hw.Note("Temperatur-Farbe: unter 45 °C blau · 60 °C grün · 75 °C gelb · ab 88 °C rot");
        hw.Check("Beim Start von Toast Temperatur die Einstellungen anwenden", st.ApplyOnStart, v => { st.ApplyOnStart = v; st.Save(); });
        hw.Buttons(("Im Mainboard speichern", () =>
        {
            bool ok = _svc.Device!.Commit();
            MessageBox.Show(FindForm(), ok
                ? "Gespeichert. Die Beleuchtung bleibt jetzt auch ohne Toast Temperatur und nach einem Neustart so.\n(„Temperatur als Farbe“ funktioniert nur, solange Toast Temperatur läuft.)"
                : "Speichern fehlgeschlagen: " + _svc.Device!.LastError, "RGB");
        }));
        Add(hw);

        var zones = st.Sync ? _svc.Device!.Zones.Take(1).ToList() : _svc.Device!.Zones;
        foreach (var z in zones)
        {
            Add(new SectionHeader(st.Sync ? "Alle Anschlüsse" : z.Name, Theme.Muted) { Height = S(34), Margin = new Padding(S(6), S(10), S(6), S(2)) });
            Add(BuildZone(z, st.Sync));
        }
    }

    void BuildCustom()
    {
        var st = _svc.Settings;
        var fx = st.Effect;
        void Save()
        {
            st.Save();
            if (_svc.BoardNative && _svc.Device != null) Debounce(() => Task.Run(_svc.ApplyBoardFromCustom));
        }

        Add(new SectionHeader("Eigener Effekt", Theme.Muted) { Height = S(34), Margin = new Padding(S(6), S(10), S(6), S(2)) });
        Add(new LedPreview(_svc.Engine) { Height = S(46), Margin = new Padding(S(6), S(2), S(6), S(4)) });

        var c = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        var presetNames = new[] { "– Voreinstellung wählen –" }.Concat(CustomEffect.Presets.Select(p => p.Name)).ToArray();
        c.Combo("Voreinstellung:", presetNames, 0, i =>
        {
            if (i <= 0) return;
            st.Effect = CustomEffect.Presets[i - 1].Effect.Clone();
            Save();
            RebuildSoon();
        });
        var types = Enum.GetValues<EffectType>();
        c.Combo("Effekt:", types.Select(CustomEffect.Name).ToArray(), Array.IndexOf(types, fx.Type), i => { fx.Type = types[i]; Save(); RebuildSoon(); });
        c.Note(fx.Type switch
        {
            EffectType.Breathing => "Wird heller und dunkler, bei jedem Atemzug die nächste Farbe.",
            EffectType.Wave => "Die Farben laufen als Welle durch die LEDs.",
            EffectType.RainbowWave => "Alle Regenbogenfarben laufen durch (Farben unten werden nicht genutzt).",
            EffectType.Gradient => "Farbverlauf über alle LEDs – bei Geschwindigkeit 0 steht er still.",
            EffectType.Comet => "Ein Lichtpunkt mit Schweif läuft durch. Farbe 1 = Komet, Farbe 2 = Hintergrund.",
            EffectType.Sparkle => "Zufällige LEDs funkeln auf. Letzte Farbe = Hintergrund (ab 3 Farben).",
            EffectType.ColorCycle => "Alle LEDs wechseln gemeinsam durch die Farben.",
            EffectType.Fire => "Flackern wie Feuer aus den gewählten Farben.",
            _ => "Farbe zeigt die Temperatur (blau → rot), dazu eine sanfte Welle.",
        });
        Add(c);

        if (fx.Type is not (EffectType.RainbowWave or EffectType.TempWave))
        {
            Add(new SectionHeader("Farben", Theme.Muted) { Height = S(30), Margin = new Padding(S(6), S(6), S(6), S(2)) });
            Add(new ColorListEditor(fx.Colors, Save) { Margin = new Padding(S(6), S(2), S(6), S(4)) });
        }

        var p = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        p.Numbers(
            ("Helligkeit", (float)fx.Brightness, 5, 100, "%", (Action<float>)(v => { fx.Brightness = (int)v; Save(); })),
            ("Dunkelste Stufe", (float)fx.MinLevel, 0, 100, "%", (Action<float>)(v => { fx.MinLevel = (int)v; Save(); })));
        p.Note("Dunkelste Stufe: 0 % = geht ganz aus, höher = wird nur gedimmt.");
        p.Numbers(
            ("Weichheit / Fade", (float)fx.Softness, 0, 100, "%", (Action<float>)(v => { fx.Softness = (int)v; Save(); })),
            ("Geschwindigkeit", (float)fx.Speed, 0, 100, "%", (Action<float>)(v => { fx.Speed = (int)v; Save(); })));
        p.Note("Weichheit: 0 % = harte Wechsel / Blinken, 100 % = ganz weiche Übergänge.");
        if (fx.Type == EffectType.Breathing)
        {
            p.Note("Atmen – Zeiten in Millisekunden (0 = automatisch aus der Geschwindigkeit):", muted: false);
            var a = p.Numbers(
                ("Einblenden", (float)fx.FadeInMs, 0, 20000, "ms", (Action<float>)(v => { fx.FadeInMs = (int)v; Save(); })),
                ("Hell halten", (float)fx.HoldOnMs, 0, 20000, "ms", (Action<float>)(v => { fx.HoldOnMs = (int)v; Save(); })));
            var b = p.Numbers(
                ("Ausblenden", (float)fx.FadeOutMs, 0, 20000, "ms", (Action<float>)(v => { fx.FadeOutMs = (int)v; Save(); })),
                ("Dunkel halten", (float)fx.HoldOffMs, 0, 20000, "ms", (Action<float>)(v => { fx.HoldOffMs = (int)v; Save(); })));
            foreach (var n in a.Concat(b)) { n.Increment = 100; n.Width = S(70); }
            p.Note("Beispiel „nie ganz aus, höchstens 80 %“: Helligkeit 80 %, Dunkelste Stufe 30 %.");
        }
        else
            p.Numbers(("Breite (LEDs je Welle / Schweif)", (float)fx.Width, 2, 100, "", (Action<float>)(v => { fx.Width = (int)v; Save(); })));
        p.Check("Richtung umkehren", fx.Reverse, v => { fx.Reverse = v; Save(); });
        p.Check("Von der Mitte aus (gespiegelt)", fx.Mirror, v => { fx.Mirror = v; Save(); });
        if (fx.Type == EffectType.TempWave)
        {
            string[] sources = { "CPU", "GPU", "Hotspot", "Max" };
            p.Combo("Temperatur von:", new[] { "CPU", "Grafikkarte", "GPU-Hot-Spot", "Höchster Wert von CPU und GPU" },
                Array.IndexOf(sources, st.TempSource), i => { st.TempSource = sources[i]; Save(); });
        }
        p.Note("Eigene Effekte laufen, solange Toast Temperatur läuft. Beim Beenden übernimmt das Mainboard mit den Mainboard-Effekten.");
        Add(p);
    }

    Control BuildZone(RgbZone z, bool all)
    {
        var s = _svc.Settings.For(z.Index);
        var card = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        int modeIdx = Math.Max(0, Array.FindIndex(RgbService.Modes, m => m.Mode == s.Mode));

        void Changed()
        {
            if (all)
            {
                // Einstellungen der ersten Zone auf alle übertragen
                foreach (var other in _svc.Device!.Zones)
                {
                    var o = _svc.Settings.For(other.Index);
                    o.Mode = s.Mode; o.Color = s.Color; o.Speed = s.Speed;
                }
            }
            _svc.Settings.Save();
            Debounce(() => { if (all) _svc.ApplyAll(); else _svc.Apply(z); });
        }

        card.Combo("Effekt:", RgbService.Modes.Select(m => m.Name).ToArray(), modeIdx, i => { s.Mode = RgbService.Modes[i].Mode; Changed(); });
        Button? colorBtn = null;
        card.Buttons(("Farbe wählen …", () =>
        {
            using var dlg = new ColorDialog { Color = Color.FromArgb(s.Color), FullOpen = true };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            s.Color = dlg.Color.ToArgb();
            if (colorBtn != null) colorBtn.BackColor = dlg.Color;
            Changed();
        }));
        colorBtn = card.Controls.OfType<Button>().LastOrDefault();
        if (colorBtn != null) { colorBtn.BackColor = Color.FromArgb(s.Color); colorBtn.ForeColor = Color.FromArgb(s.Color).GetBrightness() > 0.55f ? Color.Black : Color.White; }
        card.Numbers(("Geschwindigkeit", (float)s.Speed, 0, 100, "%", (Action<float>)(v => { s.Speed = (int)v; Changed(); })));
        if (!_svc.Settings.Custom)
        {
            card.Note("Mainboard-Effekte kann das Mainboard nur mit Farbe und Tempo. Helligkeit, „nicht ganz aus“, " +
                      "Weichheit und die Atmen-Zeiten gibt es bei den eigenen Effekten – deine Auswahl wird übernommen:");
            card.Buttons(("Mit allen Einstellungen weiter →", () => ToCustom(s)));
        }
        if (!all && z.Kind == RgbZoneKind.Addressable)
            card.Numbers(("Anzahl LEDs", (float)z.LedCount, 1, 100, "", (Action<float>)(v => Debounce(() => _svc.Device!.SetLedCount(z, (int)v)))));
        return card;
    }

    /// Mainboard-Effekt in einen gleichwertigen eigenen Effekt übernehmen und umschalten
    void ToCustom(RgbZoneSettings z)
    {
        var fx = _svc.Settings.Effect;
        var c = Color.FromArgb(z.Color);
        (EffectType type, int softness) = z.Mode switch
        {
            0x02 => (EffectType.Breathing, 80),
            0x03 => (EffectType.Breathing, 0),
            0x04 => (EffectType.ColorCycle, 80),
            0x05 => (EffectType.Sparkle, 60),
            0x07 or 0x0E => (EffectType.RainbowWave, 80),
            0x08 or 0x0B or 0x0C => (EffectType.Comet, 70),
            0x0D => (EffectType.Wave, 80),
            RgbSettings.TempMode => (EffectType.TempWave, 80),
            _ => (EffectType.Gradient, 80),
        };
        fx.Type = type;
        fx.Softness = softness;
        fx.Speed = Math.Clamp(z.Speed, 1, 100);
        fx.Brightness = z.Mode == 0x00 ? 5 : Math.Max(fx.Brightness, 5);
        if (type is EffectType.Breathing or EffectType.Gradient or EffectType.Comet or EffectType.Wave or EffectType.Sparkle)
            fx.Colors = new List<int> { c.ToArgb() };
        _svc.SetCustom(true);
        RebuildSoon();
    }

    System.Windows.Forms.Timer? _debounce;
    Action? _pending;

    void Debounce(Action a)
    {
        _pending = a;
        _debounce ??= new System.Windows.Forms.Timer { Interval = 200 };
        _debounce.Stop();
        _debounce.Tick -= OnDebounce;
        _debounce.Tick += OnDebounce;
        _debounce.Start();
    }

    void OnDebounce(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        var a = _pending;
        _pending = null;
        if (a != null) Task.Run(() => { try { a(); } catch (Exception ex) { RgbSettings.Log("Fehler: " + ex.Message); } });
    }

    void RebuildSoon() => BeginInvoke(() =>
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
        Controls.Clear();
        _full.Clear();
        Build();
        ResumeLayout();
    });
}

// ===========================================================================
// Eigene Effekte (laufen in Toast Temperatur, jede LED einzeln)
// ===========================================================================
enum EffectType { Breathing, Wave, RainbowWave, Gradient, Comet, Sparkle, ColorCycle, Fire, TempWave }

sealed class CustomEffect
{
    public EffectType Type { get; set; } = EffectType.Wave;
    public List<int> Colors { get; set; } = new();
    public int Speed { get; set; } = 50;        // 1–100
    public int Brightness { get; set; } = 100;  // 5–100
    public int Width { get; set; } = 20;        // LEDs je Wellenlänge / Schweif
    public bool Reverse { get; set; }
    /// Dunkelste Stufe 0–100 (0 = geht ganz aus)
    public int MinLevel { get; set; }
    /// Weichheit / Fade 0–100 (0 = harte Wechsel, 100 = ganz weich)
    public int Softness { get; set; } = 80;
    /// Von der Mitte nach außen (gespiegelt)
    public bool Mirror { get; set; }
    /// Atmen: Zeiten in Millisekunden (0 = automatisch aus der Geschwindigkeit)
    public int FadeInMs { get; set; }
    public int HoldOnMs { get; set; }
    public int FadeOutMs { get; set; }
    public int HoldOffMs { get; set; }

    public CustomEffect Clone() => new()
    {
        Type = Type, Colors = Colors.ToList(), Speed = Speed, Brightness = Brightness, Width = Width, Reverse = Reverse,
        MinLevel = MinLevel, Softness = Softness, Mirror = Mirror,
        FadeInMs = FadeInMs, HoldOnMs = HoldOnMs, FadeOutMs = FadeOutMs, HoldOffMs = HoldOffMs,
    };

    public static string Name(EffectType t) => t switch
    {
        EffectType.Breathing => "Atmen",
        EffectType.Wave => "Welle",
        EffectType.RainbowWave => "Regenbogen-Welle",
        EffectType.Gradient => "Farbverlauf",
        EffectType.Comet => "Lauflicht (Komet)",
        EffectType.Sparkle => "Funkeln",
        EffectType.ColorCycle => "Farbwechsel",
        EffectType.Fire => "Feuer",
        _ => "Temperatur-Welle",
    };

    static int C(int r, int g, int b) => System.Drawing.Color.FromArgb(r, g, b).ToArgb();

    public static readonly (string Name, CustomEffect Effect)[] Presets =
    {
        ("Ozean", new() { Type = EffectType.Wave, Colors = { C(0, 40, 255), C(0, 200, 255), C(0, 255, 160) }, Speed = 35, Width = 30 }),
        ("Polarlicht", new() { Type = EffectType.Wave, Colors = { C(0, 255, 120), C(40, 80, 255), C(190, 0, 255) }, Speed = 25, Width = 40 }),
        ("Feuer", new() { Type = EffectType.Fire, Colors = { C(255, 30, 0), C(255, 120, 0), C(255, 200, 40) }, Speed = 60, Width = 10 }),
        ("Neon-Atmen", new() { Type = EffectType.Breathing, Colors = { C(255, 0, 200), C(0, 230, 255) }, Speed = 40 }),
        ("Sonnenuntergang", new() { Type = EffectType.Gradient, Colors = { C(255, 60, 0), C(255, 0, 90), C(120, 0, 255) }, Speed = 15, Width = 30 }),
        ("Regenbogen", new() { Type = EffectType.RainbowWave, Speed = 45, Width = 30 }),
        ("Komet", new() { Type = EffectType.Comet, Colors = { C(255, 255, 255), C(0, 60, 255) }, Speed = 55, Width = 12 }),
        ("Sternenhimmel", new() { Type = EffectType.Sparkle, Colors = { C(255, 255, 255), C(120, 160, 255), C(10, 10, 40) }, Speed = 50 }),
        ("Farbwechsel", new() { Type = EffectType.ColorCycle, Colors = { C(255, 0, 0), C(0, 255, 0), C(0, 80, 255) }, Speed = 30 }),
        ("Temperatur-Welle", new() { Type = EffectType.TempWave, Speed = 30, Width = 25 }),
    };
}

static class EffectRenderer
{
    static readonly Random Rnd = new();
    static readonly Dictionary<(int, int), float> Sparks = new();

    static Color Lerp(Color a, Color b, float f)
    {
        f = Math.Clamp(f, 0, 1);
        return Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
    }

    static Color Scale(Color c, float f)
    {
        f = Math.Clamp(f, 0, 1);
        return Color.FromArgb((int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
    }

    static float _soft = 0.8f;

    /// Übergang formen: weich = linear, hart = Sprung in der Mitte
    static float Shape(float f)
    {
        float w = Math.Max(0.02f, _soft);
        return Math.Clamp((f - 0.5f) / w + 0.5f, 0, 1);
    }

    /// Farbe an Position p (0..1, zyklisch) im Farbverlauf der Palette
    static Color Palette(List<Color> pal, float p, bool cyclic = true)
    {
        if (pal.Count == 0) return Color.White;
        if (pal.Count == 1) return pal[0];
        p -= MathF.Floor(p);
        int n = cyclic ? pal.Count : pal.Count - 1;
        float x = p * n;
        int i = (int)x;
        return Lerp(pal[i % pal.Count], pal[(i + 1) % pal.Count], Shape(x - i));
    }

    public static Color Hue(float h)
    {
        h = (h % 360 + 360) % 360 / 60f;
        float x = 1 - MathF.Abs(h % 2 - 1);
        (float r, float g, float b) = (int)h switch
        {
            0 => (1f, x, 0f), 1 => (x, 1f, 0f), 2 => (0f, 1f, x), 3 => (0f, x, 1f), 4 => (x, 0f, 1f), _ => (1f, 0f, x),
        };
        return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    /// LED-Farben einer Zone zum Zeitpunkt t (Sekunden). temp = aktuelle Temperatur (für Temperatur-Effekte)
    public static Color[] Render(CustomEffect fx, int zone, int count, double t, float? temp)
    {
        var res = new Color[Math.Max(1, count)];
        var pal = fx.Colors.Select(Color.FromArgb).ToList();
        if (pal.Count == 0) pal.Add(Color.FromArgb(0, 120, 255));
        float speed = Math.Clamp(fx.Speed, 1, 100) / 100f;
        float width = Math.Max(2, fx.Width);
        float bright = Math.Clamp(fx.Brightness, 5, 100) / 100f;
        float dir = fx.Reverse ? -1 : 1;
        float min = Math.Clamp(fx.MinLevel, 0, 100) / 100f;
        _soft = Math.Clamp(fx.Softness, 0, 100) / 100f;
        int n = res.Length;
        float Level(float l) => min + (1 - min) * Math.Clamp(l, 0, 1);

        for (int idx = 0; idx < n; idx++)
        {
            // Von der Mitte aus: beide Hälften laufen gespiegelt
            int i = fx.Mirror ? (int)MathF.Round(MathF.Abs(idx - (n - 1) / 2f)) : idx;
            float pos = i / width;
            Color c;
            switch (fx.Type)
            {
                case EffectType.Breathing:
                {
                    // Einblenden – hell halten – Ausblenden – dunkel halten (frei einstellbar, sonst aus der Geschwindigkeit)
                    float auto = (9f - speed * 7.5f) / 2f;
                    float fin = fx.FadeInMs > 0 ? fx.FadeInMs / 1000f : auto;
                    float hon = fx.HoldOnMs / 1000f;
                    float fout = fx.FadeOutMs > 0 ? fx.FadeOutMs / 1000f : auto;
                    float hoff = fx.HoldOffMs / 1000f;
                    float period = Math.Max(0.1f, fin + hon + fout + hoff);
                    double cycle = t / period;
                    int k = (int)Math.Floor(cycle);
                    float x = (float)(cycle - k) * period;
                    float level;
                    if (x < fin) level = x / Math.Max(0.001f, fin);
                    else if (x < fin + hon) level = 1;
                    else if (x < fin + hon + fout) level = 1 - (x - fin - hon) / Math.Max(0.001f, fout);
                    else level = 0;
                    // Weichheit: weich = sanfte Kurve, hart = lineares/abruptes Ein- und Ausblenden
                    level = _soft >= 0.5f ? (1 - MathF.Cos(level * MathF.PI)) / 2 : (_soft < 0.1f ? (level > 0.5f ? 1 : 0) : level);
                    // Wahrnehmung: das Auge sieht dunkle Stufen viel stärker -> quadratisch, damit das Ein-/Ausblenden gleichmäßig wirkt
                    level *= level;
                    var cur = pal[((k % pal.Count) + pal.Count) % pal.Count];
                    // Mehrere Farben und „nicht ganz aus“: beim Ausblenden weich zur nächsten Farbe überblenden statt zu springen
                    if (pal.Count > 1 && min > 0 && x >= fin + hon)
                    {
                        var next = pal[(((k + 1) % pal.Count) + pal.Count) % pal.Count];
                        float f = Math.Clamp((x - fin - hon) / Math.Max(0.001f, fout + hoff), 0, 1);
                        cur = Lerp(cur, next, (1 - MathF.Cos(f * MathF.PI)) / 2);
                    }
                    c = Scale(cur, Level(level));
                    break;
                }
                case EffectType.Wave:
                    c = Palette(pal, pos - dir * (float)(t * (0.05 + speed * 1.2)));
                    break;
                case EffectType.RainbowWave:
                    c = Hue(pos * 360f - dir * (float)(t * (20 + speed * 300)));
                    break;
                case EffectType.Gradient:
                    c = Palette(pal, (float)i / Math.Max(1, n - 1) * 0.999f - dir * (float)(t * speed * 0.15), cyclic: speed > 0.05f);
                    break;
                case EffectType.Comet:
                {
                    float span = n + width;
                    float head = (float)((t * (3 + speed * 60)) % span);
                    if (fx.Reverse) head = span - head;
                    float d = fx.Reverse ? i - head : head - i;
                    float tail = d >= 0 && d < width ? 1 - d / width : 0;
                    tail = _soft < 0.3f ? (tail > 0 ? 1 : 0) : MathF.Pow(tail, 2.5f - _soft * 1.5f);
                    var bg = pal.Count > 1 ? Scale(pal[1], Math.Max(min, 0.02f)) : Scale(pal[0], min);
                    c = Lerp(bg, pal[0], tail);
                    break;
                }
                case EffectType.Sparkle:
                {
                    var bg = Scale(pal.Count > 2 ? pal[^1] : Color.FromArgb(40, 40, 90), Math.Max(min, 0.05f));
                    var key = (zone, idx);
                    Sparks.TryGetValue(key, out float lvl);
                    // Weichheit = wie langsam ein Funke verglimmt
                    lvl = Math.Max(0, lvl - (0.02f + (1 - _soft) * 0.25f) - speed * 0.05f);
                    if (Rnd.NextDouble() < 0.004 + speed * 0.03) lvl = 1;
                    Sparks[key] = lvl;
                    var sparkCol = pal[(i * 7 + zone) % Math.Max(1, pal.Count > 2 ? pal.Count - 1 : pal.Count)];
                    c = Lerp(bg, sparkCol, lvl);
                    break;
                }
                case EffectType.ColorCycle:
                    c = Palette(pal, (float)(t * (0.02 + speed * 0.3)));
                    break;
                case EffectType.Fire:
                {
                    float noise = (float)(Math.Sin(i * 1.7 + t * (3 + speed * 9)) * 0.5 + Math.Sin(i * 0.6 - t * (2 + speed * 5)) * 0.3
                                          + Rnd.NextDouble() * 0.4);
                    c = Scale(Palette(pal, Math.Clamp(noise * 0.5f + 0.4f, 0, 0.999f), cyclic: false), Level(0.3f + noise * 0.6f));
                    break;
                }
                default:   // Temperatur-Welle
                {
                    var tc = RgbService.TempColor(temp ?? 50);
                    float wave = 0.5f + 0.5f * MathF.Sin(pos * MathF.PI * 2 - dir * (float)(t * (1 + speed * 6)));
                    c = Scale(tc, Level(_soft >= 0.98f ? wave : Shape(wave)));
                    break;
                }
            }
            res[idx] = Scale(c, bright);
        }
        return res;
    }
}

/// Spielt den eigenen Effekt ab (ca. 20 Bilder pro Sekunde, eigener Hintergrund-Thread)
sealed class EffectEngine
{
    readonly RgbService _svc;
    Thread? _thread;
    volatile bool _run;
    public Color[]? Preview { get; private set; }

    public EffectEngine(RgbService svc) => _svc = svc;
    public bool Running => _run;

    public void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "RGB-Effekte" };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(1500);
        _thread = null;
    }

    void Loop()
    {
        var active = new HashSet<IRgbTarget>();
        var fails = new Dictionary<IRgbTarget, int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_run)
        {
            long start = sw.ElapsedMilliseconds;
            try
            {
                var fx = _svc.Settings.Effect;
                float? temp = _svc.Temperature();
                double t = sw.Elapsed.TotalSeconds;
                Preview = EffectRenderer.Render(fx, 999, 60, t, temp);

                List<IRgbTarget> targets;
                lock (_svc.Targets) targets = _svc.Targets.ToList();
                for (int ti = 0; ti < targets.Count; ti++)
                {
                    var target = targets[ti];
                    bool on = _svc.IsEnabled(target);
                    if (!on)
                    {
                        if (active.Remove(target)) { try { target.End(); } catch { } }
                        continue;
                    }
                    if (active.Add(target)) { try { target.Begin(); } catch (Exception ex) { RgbSettings.Log($"{target.Name}: {ex.Message}"); } }
                    var segs = target.Segments;
                    var frame = new List<Color[]>(segs.Length);
                    for (int si = 0; si < segs.Length; si++)
                        frame.Add(EffectRenderer.Render(fx, ti * 16 + si, segs[si], t, temp));
                    if (target is not PolychromeTarget && _svc.Settings.SwapRG.Contains(target.Id))   // Mainboard tauscht selbst (SwapRgFix)
                        foreach (var seg in frame)
                            for (int k = 0; k < seg.Length; k++) seg[k] = Color.FromArgb(seg[k].G, seg[k].R, seg[k].B);
                    if (!target.Write(frame))
                    {
                        fails[target] = fails.GetValueOrDefault(target) + 1;
                        if (fails[target] == 40) RgbSettings.Log($"{target.Name}: Bilder kommen nicht an");
                    }
                    else fails[target] = 0;
                }
                // entfernte Geräte beenden
                foreach (var gone in active.Where(a => !targets.Contains(a)).ToList()) active.Remove(gone);
            }
            catch (Exception ex) { RgbSettings.Log("Effekt: " + ex.Message); }
            int wait = 50 - (int)(sw.ElapsedMilliseconds - start);
            if (wait > 0) Thread.Sleep(wait);
        }
        foreach (var a in active) { try { a.End(); } catch { } }
    }
}

/// Live-Vorschau des eigenen Effekts als Leiste
sealed class LedPreview : Control
{
    readonly EffectEngine _engine;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };

    public LedPreview(EffectEngine engine)
    {
        _engine = engine;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        _timer.Tick += (_, _) => { if (Visible) Invalidate(); };
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var cols = _engine.Preview;
        if (cols == null || cols.Length == 0)
        {
            TextRenderer.DrawText(g, "Vorschau startet …", Font, ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        float w = (Width - 20f) / cols.Length;
        float d = Math.Min(w * 0.8f, Height - 16f);
        for (int i = 0; i < cols.Length; i++)
        {
            float x = 10 + i * w + (w - d) / 2, y = (Height - d) / 2;
            var c = cols[i];
            using (var glow = new SolidBrush(Color.FromArgb(60, c))) g.FillEllipse(glow, x - d * 0.3f, y - d * 0.3f, d * 1.6f, d * 1.6f);
            using var b = new SolidBrush(c);
            g.FillEllipse(b, x, y, d, d);
        }
    }
}

/// Farbliste: bis zu 6 Farben, anklicken zum Ändern, + / – zum Hinzufügen und Entfernen
sealed class ColorListEditor : Panel
{
    readonly List<int> _colors;
    readonly Action _save;
    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public ColorListEditor(List<int> colors, Action save)
    {
        _colors = colors;
        _save = save;
        if (_colors.Count == 0) _colors.Add(Color.FromArgb(0, 120, 255).ToArgb());
        BackColor = Theme.Card;
        Height = 60;
        Rebuild();
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Rebuild();
    }

    void Rebuild()
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); }
        int x = S(14), y = S(10), sz = S(40);
        for (int i = 0; i < _colors.Count; i++)
        {
            int idx = i;
            var col = Color.FromArgb(_colors[i]);
            var b = new Button
            {
                BackColor = col, FlatStyle = FlatStyle.Flat, Location = new Point(x, y), Size = new Size(sz, sz),
                Cursor = Cursors.Hand, Text = "",
            };
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.BorderSize = 2;
            new ToolTip().SetToolTip(b, $"Farbe {i + 1} – klicken zum Ändern, Rechtsklick zum Entfernen");
            b.Click += (_, _) =>
            {
                using var dlg = new ColorDialog { Color = col, FullOpen = true };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                _colors[idx] = dlg.Color.ToArgb();
                _save();
                BeginInvoke(Rebuild);
            };
            b.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right || _colors.Count <= 1) return;
                _colors.RemoveAt(idx);
                _save();
                BeginInvoke(Rebuild);
            };
            Controls.Add(b);
            x += sz + S(10);
        }
        if (_colors.Count < 6)
        {
            var add = PageBar.Button("+ Farbe", K, 100);
            add.Location = new Point(x + S(6), y + (sz - S(30)) / 2);
            add.Height = S(30);
            add.Click += (_, _) =>
            {
                var last = Color.FromArgb(_colors[^1]);
                // neue Farbe: Farbton der letzten um 60° weiterdrehen
                float h = last.GetHue() + 60;
                _colors.Add(EffectRenderer.Hue(h).ToArgb());
                _save();
                BeginInvoke(Rebuild);
            };
            Controls.Add(add);
            x = add.Right + S(14);
        }
        var hint = new Label
        {
            Text = "Klick = Farbe ändern · Rechtsklick = entfernen", ForeColor = Theme.Muted, AutoSize = true,
            Location = new Point(x + S(6), y + sz / 2 - S(8)), UseMnemonic = false,
        };
        Controls.Add(hint);
        Height = y + sz + S(10);
        ResumeLayout();
    }
}

/// Andere RGB-Programme, die gleichzeitig dieselben Geräte steuern würden
static class RgbRivals
{
    static readonly (string Process, string Name)[] Known =
    {
        ("RazerCentralService", "Razer Synapse"), ("Razer Synapse", "Razer Synapse"), ("Razer Synapse 3", "Razer Synapse"),
        ("RazerAppEngine", "Razer Synapse"), ("Synapse3", "Razer Synapse"), ("Razer Chroma SDK Service", "Razer Chroma"),
        ("ROCCAT_Swarm", "ROCCAT Swarm"), ("ROCCAT_Swarm_Monitor", "ROCCAT Swarm"), ("Roccat Swarm", "ROCCAT Swarm"),
        ("PolyChromeRGB", "ASRock Polychrome RGB"), ("ASRPolychromeRGB", "ASRock Polychrome RGB"), ("AsrPolychromeSync", "ASRock Polychrome RGB"),
        ("iCUE", "Corsair iCUE"), ("lghub", "Logitech G HUB"), ("lghub_agent", "Logitech G HUB"),
        ("ArmouryCrate", "ASUS Armoury Crate"), ("LightingService", "ASUS Aura"), ("MysticLight", "MSI Mystic Light"),
        ("SignalRgb", "SignalRGB"), ("NZXT CAM", "NZXT CAM"), ("SteelSeriesGG", "SteelSeries GG"),
    };

    public static List<string> Running()
    {
        try
        {
            var names = System.Diagnostics.Process.GetProcesses().Select(p => { using (p) return p.ProcessName; })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Known.Where(k => names.Contains(k.Process)).Select(k => k.Name).Distinct().ToList();
        }
        catch { return new List<string>(); }
    }
}
