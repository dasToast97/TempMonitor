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
    public bool RgSwap { get; init; }
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
    byte[]? Transfer(byte[] data)
    {
        lock (_gate)
        {
            try
            {
                var buf = new byte[_dev.OutLength];
                Array.Copy(data, 0, buf, 1, Math.Min(data.Length, buf.Length - 1));   // buf[0] = Report-ID 0
                _dev.Stream.Write(buf, 0, buf.Length);
                var inBuf = new byte[_dev.InLength];
                using var cts = new CancellationTokenSource(600);
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

    /// Effekt einer Zone setzen. speed 0–100 (100 = schnell)
    public bool SetZone(RgbZone z, byte mode, Color c, int speed)
    {
        byte sp = (byte)Math.Clamp(255 - speed * 255 / 100, 0, 255);
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
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<RgbSettings>(File.ReadAllText(FilePath)) ?? new(); }
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
    public PolychromeUsb Device { get; }
    public RgbSettings Settings { get; }
    readonly Func<KeySensors?> _keys;
    readonly Dictionary<int, Color> _lastTempColor = new();

    public RgbService(PolychromeUsb dev, Func<KeySensors?> keys)
    {
        Device = dev;
        _keys = keys;
        Settings = RgbSettings.Load();
        if (Settings.ApplyOnStart) Task.Run(ApplyAll);
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

    public void ApplyAll()
    {
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
        if (_keys()?.Value(Settings.TempSource) is not float t) return;
        var col = TempColor(t);
        foreach (var z in Device.Zones)
        {
            if (Settings.For(z.Index).Mode != RgbSettings.TempMode) continue;
            if (!force && _lastTempColor.TryGetValue(z.Index, out var last)
                && Math.Abs(last.R - col.R) + Math.Abs(last.G - col.G) + Math.Abs(last.B - col.B) < 12) continue;
            if (Device.SetZone(z, 0x01, col, 50)) _lastTempColor[z.Index] = col;
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
        Build();
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
        Add(new SectionHeader("RGB-Beleuchtung · ASRock Polychrome", Color.FromArgb(244, 114, 182)) { Height = S(40), Margin = new Padding(S(6), S(14), S(6), S(4)) });
        var top = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        top.Note("Steuert die Beleuchtung über das Mainboard. Am ARGB-Anschluss mit Hub: Ein Verteiler-Hub schickt allen Geräten dasselbe Signal – " +
                 "als LED-Anzahl die des längsten angeschlossenen Geräts eintragen (oder die Summe, wenn die Geräte hintereinander geschaltet sind).");
        top.Check("Alle Anschlüsse gleich einstellen", st.Sync, v => { st.Sync = v; st.Save(); RebuildSoon(); });
        string[] sources = { "CPU", "GPU", "Hotspot", "Max" };
        top.Combo("„Temperatur als Farbe“ nach:", new[] { "CPU", "Grafikkarte", "GPU-Hot-Spot", "Höchster Wert von CPU und GPU" },
            Array.IndexOf(sources, st.TempSource), i => { st.TempSource = sources[i]; st.Save(); _svc.TickTemperature(force: true); });
        top.Note("Temperatur-Farbe: unter 45 °C blau · 60 °C grün · 75 °C gelb · ab 88 °C rot");
        top.Check("Beim Start von Toast Temperatur die Einstellungen anwenden", st.ApplyOnStart, v => { st.ApplyOnStart = v; st.Save(); });
        top.Buttons(("Im Mainboard speichern", () =>
        {
            bool ok = _svc.Device.Commit();
            MessageBox.Show(FindForm(), ok
                ? "Gespeichert. Die Beleuchtung bleibt jetzt auch ohne Toast Temperatur und nach einem Neustart so.\n(„Temperatur als Farbe“ funktioniert nur, solange Toast Temperatur läuft.)"
                : "Speichern fehlgeschlagen: " + _svc.Device.LastError, "RGB");
        }));
        Add(top);

        var zones = st.Sync ? _svc.Device.Zones.Take(1).ToList() : _svc.Device.Zones;
        foreach (var z in zones)
        {
            string title = st.Sync ? "Alle Anschlüsse" : z.Name;
            Add(new SectionHeader(title, Theme.Muted) { Height = S(34), Margin = new Padding(S(6), S(10), S(6), S(2)) });
            Add(BuildZone(z, st.Sync));
        }
        if (st.Sync)
        {
            var info = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
            info.Note("Erkannt: " + string.Join(" · ", _svc.Device.Zones.Select(z => z.Kind == RgbZoneKind.Addressable ? $"{z.Name} ({z.LedCount} LEDs)" : z.Name)));
            foreach (var z in _svc.Device.Zones.Where(z => z.Kind == RgbZoneKind.Addressable))
            {
                var zz = z;
                info.Numbers(($"LEDs an {z.Name}", (float)z.LedCount, 1, 100, "", (Action<float>)(v => Debounce(() => _svc.Device.SetLedCount(zz, (int)v)))));
            }
            Add(info);
        }
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
                foreach (var other in _svc.Device.Zones)
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
        if (!all && z.Kind == RgbZoneKind.Addressable)
            card.Numbers(("Anzahl LEDs", (float)z.LedCount, 1, 100, "", (Action<float>)(v => Debounce(() => _svc.Device.SetLedCount(z, (int)v)))));
        return card;
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
