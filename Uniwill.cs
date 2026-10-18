using System.Management;

namespace TempMonitor;

// ===========================================================================
// Uniwill-Laptops (XMG NEO, TUXEDO Stellaris, Schenker … – Control Center mit GCUService)
// Zugriff auf den Embedded Controller über WMI (GUID ABBC0F6F-8EA1-11D1-00A0-C90629100000,
// Methode 4) – dieselbe Schnittstelle, die das Control Center und der TUXEDO-Treiber nutzen.
//   Lesen:  arg = Adresse,                     Funktion 1 -> Rückgabe Low-Byte = Wert
//   Schreiben: arg = Adresse | Wert << 16,     Funktion 0
//   0xFEFEFEFE = Fehler
// Lüfter direkt steuern (wie TUXEDO auf X6FR5xxY): 0x0751 Bit 0x40 = "volle Lüftersteuerung",
// dann 0x1804 (Lüfter 1 / CPU) und 0x1809 (Lüfter 2 / GPU) = 0–200.
// ===========================================================================
sealed class UniwillEc
{
    const string Guid = "ABBC0F6F-8EA1-11D1-00A0-C90629100000";
    const ushort RegMode = 0x0751, RegManual = 0x0741, RegFan1 = 0x1804, RegFan2 = 0x1809;
    const byte FullFanBit = 0x40;
    public const int SpeedMax = 200;
    /// Zuletzt gefundene Schnittstelle (für Diagnose)
    public static UniwillEc? Current { get; private set; }

    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "laptop.log");

    readonly ManagementObject _obj;
    readonly string _method;
    readonly string _inName;
    readonly bool _inIsArray;
    readonly object _gate = new();

    public static UniwillEc? Instance { get; private set; }
    public string Board { get; private set; } = "";
    public bool Active { get; private set; }
    /// Gegenprobe fehlgeschlagen -> nichts mehr schreiben
    public bool Disabled { get; private set; }
    public string? LastError { get; private set; }

    // gewünschte Leistung 0–100 je Lüfter, -1 = nicht gesetzt
    readonly float[] _want = { -1, -1 };
    readonly float?[] _current = { null, null };
    byte? _manualBefore;
    DateTime _lastRead = DateTime.MinValue, _lastRestart = DateTime.MinValue, _lastCheck = DateTime.MinValue;

    UniwillEc(ManagementObject obj, string method, string inName, bool inIsArray)
    {
        _obj = obj;
        _method = method;
        _inName = inName;
        _inIsArray = inIsArray;
    }

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    static string BoardName()
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT Product, Manufacturer FROM Win32_BaseBoard");
            foreach (ManagementObject o in s.Get())
                using (o) return $"{o["Product"]}".Trim();
        }
        catch { }
        return "";
    }

    /// Sucht die WMI-Klasse mit der Uniwill-GUID und prüft, ob der EC antwortet
    public static UniwillEc? Probe()
    {
        try
        {
            using var classes = new ManagementObjectSearcher(new ManagementScope("root\\WMI"), new WqlObjectQuery("SELECT * FROM meta_class"));
            foreach (ManagementClass cls in classes.Get())
            {
                using (cls)
                {
                    string? g = null;
                    try { g = cls.Qualifiers["guid"]?.Value?.ToString(); } catch { }
                    if (g == null || !g.Trim('{', '}').Equals(Guid, StringComparison.OrdinalIgnoreCase)) continue;

                    string cname = cls.ClassPath.ClassName;
                    Log($"WMI-Klasse gefunden: {cname}");
                    foreach (MethodData m in cls.Methods)
                    {
                        string q = "";
                        try { q = m.Qualifiers["WmiMethodId"]?.Value?.ToString() ?? ""; } catch { }
                        var ins = m.InParameters?.Properties.Cast<PropertyData>().Select(p => $"{p.Name}:{p.Type}{(p.IsArray ? "[]" : "")}") ?? Array.Empty<string>();
                        var outs = m.OutParameters?.Properties.Cast<PropertyData>().Select(p => $"{p.Name}:{p.Type}{(p.IsArray ? "[]" : "")}") ?? Array.Empty<string>();
                        Log($"  Methode {m.Name} (Id {q}) in: {string.Join(", ", ins)} · out: {string.Join(", ", outs)}");
                    }

                    // Methode 4 bevorzugen, sonst GetSetULong / die erste Methode
                    MethodData? pick = cls.Methods.Cast<MethodData>().FirstOrDefault(m =>
                    {
                        try { return m.Qualifiers["WmiMethodId"]?.Value?.ToString() == "4"; } catch { return false; }
                    }) ?? cls.Methods.Cast<MethodData>().FirstOrDefault(m => m.Name.Contains("GetSetULong", StringComparison.OrdinalIgnoreCase))
                       ?? cls.Methods.Cast<MethodData>().FirstOrDefault();
                    if (pick?.InParameters == null) { Log("Keine passende Methode"); return null; }

                    var inp = pick.InParameters.Properties.Cast<PropertyData>().FirstOrDefault(p =>
                        p.Type == CimType.UInt64 || (p.IsArray && p.Type == CimType.UInt8));
                    if (inp == null) { Log("Eingabe-Parameter unbekannt – keine Steuerung"); return null; }

                    using var inst = new ManagementClass("root\\WMI", cname, null);
                    var obj = inst.GetInstances().Cast<ManagementObject>().FirstOrDefault();
                    if (obj == null) { Log("Keine Instanz"); return null; }

                    var ec = new UniwillEc(obj, pick.Name, inp.Name, inp.IsArray) { Board = BoardName() };
                    // Lesetest
                    if (ec.Read(RegMode) is not byte mode) { Log("EC antwortet nicht – keine Steuerung"); return null; }
                    byte? id = ec.Read(0x0740);
                    ec._current[0] = ec.Read(RegFan1) is byte f1 ? f1 * 100f / SpeedMax : null;
                    ec._current[1] = ec.Read(RegFan2) is byte f2 ? f2 * 100f / SpeedMax : null;
                    Log($"EC bereit · Board {ec.Board} · Barebone-ID 0x{id:X2} · Modus 0x{mode:X2} · " +
                        $"Lüfter {ec._current[0]:0} % / {ec._current[1]:0} % · Methode {pick.Name}({inp.Name}{(inp.IsArray ? "[]" : "")})");
                    Instance = ec;
                    Current = ec;
                    return ec;
                }
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.NotFound or ManagementStatus.InvalidNamespace) { }
        catch (Exception ex) { Log("Prüfung fehlgeschlagen: " + ex.Message); }
        return null;
    }

    uint? Call(uint arg, byte function)
    {
        lock (_gate)
        {
            try
            {
                using var inParams = _obj.GetMethodParameters(_method);
                if (_inIsArray)
                {
                    var buf = new byte[40];
                    BitConverter.GetBytes(arg).CopyTo(buf, 0);
                    buf[5] = function;
                    inParams[_inName] = buf;
                }
                else
                {
                    inParams[_inName] = (ulong)arg | ((ulong)function << 40);
                }
                using var outParams = _obj.InvokeMethod(_method, inParams, null);
                if (outParams == null) return null;
                foreach (PropertyData p in outParams.Properties)
                {
                    if (p.Name == "ReturnValue" && outParams.Properties.Count > 1) continue;
                    object? v = p.Value;
                    uint r = v switch
                    {
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        uint u => u,
                        ulong ul => (uint)ul,
                        int i => (uint)i,
                        long l => (uint)l,
                        _ => 0xFEFEFEFE,
                    };
                    return r == 0xFEFEFEFE ? null : r;
                }
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }
    }

    public byte? Read(ushort addr) => Call(addr, 1) is uint r ? (byte)(r & 0xFF) : null;

    public bool Write(ushort addr, byte value)
    {
        for (int i = 0; i < 3; i++)
            if (Call((uint)addr | ((uint)value << 16), 0) != null) return true;
        Log($"Schreiben fehlgeschlagen: 0x{addr:X4} = 0x{value:X2} ({LastError})");
        return false;
    }

    public float? CurrentPercent(int fan) => _current[fan];

    /// Leistung für Lüfter 0 (CPU) oder 1 (GPU) – wird im nächsten Takt geschrieben
    public void SetPercent(int fan, float pct)
    {
        lock (_gate) _want[fan] = Math.Clamp(pct, 0, 100);
    }

    /// Diesen Lüfter nicht mehr steuern (sind beide frei, übernimmt wieder der EC / das Control Center)
    public void Unset(int fan)
    {
        lock (_gate) _want[fan] = -1;
    }

    static byte ToSpeed(float pct)
    {
        // wie TUXEDO: unter 12,5 % aus, darunter bis 25 % -> 25 % (darunter laufen die Lüfter nicht sicher an)
        if (pct < 12.5f) return 0;
        if (pct < 25f) pct = 25f;
        return (byte)Math.Round(pct / 100f * SpeedMax);
    }

    /// Einmal pro Sekunde aus der Lüftersteuerung
    public void Tick()
    {
        float w0, w1;
        lock (_gate) { w0 = _want[0]; w1 = _want[1]; }
        var now = DateTime.Now;

        if ((now - _lastRead).TotalSeconds >= 2)
        {
            _lastRead = now;
            if (Read(RegFan1) is byte f1) _current[0] = f1 * 100f / SpeedMax;
            if (Read(RegFan2) is byte f2) _current[1] = f2 * 100f / SpeedMax;
        }

        if (w0 < 0 && w1 < 0)
        {
            if (Active) Release();
            return;
        }

        if (Disabled) return;
        if (!Active)
        {
            _manualBefore = Read(RegManual);
            Write(RegManual, 0x01);
            SetFullFanMode(true);
            // Gegenprobe: ist das Bit wirklich gesetzt? Sonst stimmt die Schnittstelle nicht -> Finger weg
            if (Read(RegMode) is not byte check || (check & FullFanBit) == 0)
            {
                Disabled = true;
                SetFullFanMode(false);
                if (_manualBefore is byte mb) Write(RegManual, mb);
                Log("Gegenprobe fehlgeschlagen – der EC übernimmt die Einstellung nicht. Laptop-Lüfter werden nicht gesteuert.");
                return;
            }
            Active = true;
            _lastRestart = now;
            Log($"Lüftersteuerung übernommen (vorher Handbetrieb = {_manualBefore})");
        }
        else if ((now - _lastCheck).TotalSeconds >= 3)
        {
            // Hat das Control Center zurückgestellt? Dann wieder übernehmen.
            _lastCheck = now;
            if (Read(RegMode) is byte m && (m & FullFanBit) == 0)
            {
                Log("Volle Lüftersteuerung war aus (Control Center?) – übernehme erneut");
                SetFullFanMode(true);
            }
        }

        // Der EC fällt nach längerer Zeit zurück – wie TUXEDO alle 50 Minuten kurz neu starten
        if ((now - _lastRestart).TotalMinutes >= 50)
        {
            _lastRestart = now;
            SetFullFanMode(false);
            Thread.Sleep(10);
            SetFullFanMode(true);
        }

        if (w0 >= 0) Write(RegFan1, ToSpeed(w0));
        if (w1 >= 0) Write(RegFan2, ToSpeed(w1));
    }

    void SetFullFanMode(bool on)
    {
        if (Read(RegMode) is not byte m) return;
        byte n = on ? (byte)(m | FullFanBit) : (byte)(m & ~FullFanBit);
        if (n != m) Write(RegMode, n);
    }

    /// Steuerung an EC / Control Center zurückgeben
    public void Release()
    {
        lock (_gate) { _want[0] = _want[1] = -1; }
        if (!Active) return;
        SetFullFanMode(false);
        if (_manualBefore is byte b) Write(RegManual, b);
        Active = false;
        Log("Lüftersteuerung zurückgegeben");
    }
}

/// Ein Laptop-Lüfter als Kanal für die Lüftersteuerung
sealed class UniwillFan : IExternalFan
{
    readonly UniwillEc _ec;
    readonly int _fan;

    public UniwillFan(UniwillEc ec, int fan)
    {
        _ec = ec;
        _fan = fan;
    }

    public void SetPercent(float pct) => _ec.SetPercent(_fan, pct);
    public void Release() => _ec.Unset(_fan);
    public bool Ready => !_ec.Disabled;
    public float? LastPercent => _ec.CurrentPercent(_fan);
    public string? Problem => _ec.Disabled ? "Laptop-Lüfter: Der Embedded Controller nimmt die Steuerung nicht an – der Laptop regelt selbst (Details im Protokoll)" : null;
    public string? Alarm => null;
    public string? Note => null;
}
