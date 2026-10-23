using System.Management;

namespace TempMonitor;

// ===========================================================================
// Tongfang-Laptops (XMG, Schenker, Mechrevo, Eluktronics …) über die Firmware-
// Schnittstelle des Control Centers: WMI root\WMI → MICommonInterface.MiInterface
//   InData[32]:  [1] = 250 lesen / 251 schreiben, [3] = Funktion, ab [4] Daten
//   OutData[30]: ab [4] Daten
// Funktionen: 8 = Leistungsmodus (0 ausgewogen, 1 Leistung, 2 leise, 3 volle Drehzahl),
//             13 = Lüfterdrehzahlen, 19 = Netzteil-Art, 20 = Lüfter-Boost
// Eine eigene Lüfterkurve bietet die Schnittstelle nicht – gesteuert wird über den Modus.
// ===========================================================================
sealed class TongfangLaptop
{
    const byte Get = 250, Set = 251;
    const byte FnMode = 8, FnFans = 13, FnAcType = 19, FnBoost = 20;

    public const int ModeBalanced = 0, ModePerformance = 1, ModeQuiet = 2, ModeFull = 3;

    static readonly string LogPath = Path.Combine(
        AppPaths.Config, "laptop.log");

    readonly ManagementObject _obj;
    readonly object _gate = new();

    public int? CpuRpm { get; private set; }
    public int? GpuRpm { get; private set; }
    public int? SysRpm { get; private set; }
    public int? Mode { get; private set; }
    public bool Boost { get; private set; }
    /// Modus beim Start – wird beim Beenden zurückgesetzt
    public int? OriginalMode { get; private set; }
    public bool Changed { get; private set; }
    public string? LastError { get; private set; }

    TongfangLaptop(ManagementObject obj) => _obj = obj;

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

    public static string ModeName(int? m) => m switch
    {
        ModeQuiet => "Leise",
        ModeBalanced => "Ausgewogen",
        ModePerformance => "Leistung",
        ModeFull => "Volle Drehzahl",
        null => "–",
        _ => $"Modus {m}",
    };

    /// Prüft, ob dieser Laptop die Schnittstelle hat
    public static TongfangLaptop? Probe()
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MICommonInterface");
            foreach (ManagementObject o in s.Get())
            {
                var t = new TongfangLaptop(o);
                t.Refresh();
                t.OriginalMode = t.Mode;
                Log($"Gefunden: {o["InstanceName"]} · Modus {t.Mode} · Lüfter CPU {t.CpuRpm} / GPU {t.GpuRpm} / System {t.SysRpm}");
                return t;
            }
            Log("MICommonInterface vorhanden, aber keine Instanz");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidClass || ex.ErrorCode == ManagementStatus.NotFound)
        {
            // kein Tongfang-Laptop – ganz normal
        }
        catch (Exception ex)
        {
            Log("Prüfung fehlgeschlagen: " + ex.Message);
        }
        return null;
    }

    byte[]? Call(byte op, byte fn, params byte[] payload)
    {
        lock (_gate)
        {
            var inData = new byte[32];
            inData[1] = op;
            inData[3] = fn;
            Array.Copy(payload, 0, inData, 4, Math.Min(payload.Length, 28));
            using var inParams = _obj.GetMethodParameters("MiInterface");
            inParams["InData"] = inData;
            using var outParams = _obj.InvokeMethod("MiInterface", inParams, null);
            return outParams?["OutData"] as byte[];
        }
    }

    static int? Le16(byte[]? b, int i) => b != null && b.Length > i + 1 ? b[i] | (b[i + 1] << 8) : null;

    /// Drehzahlen und Modus lesen
    public void Refresh()
    {
        try
        {
            var f = Call(Get, FnFans);
            CpuRpm = Le16(f, 4);
            GpuRpm = Le16(f, 6);
            SysRpm = Le16(f, 10);
            var m = Call(Get, FnMode);
            if (m != null && m.Length > 4) Mode = m[4];
            LastError = null;
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    /// Leistung/volle Drehzahl geht nur am richtigen Netzteil (nicht über USB-C)
    public bool OnUsbC()
    {
        try { var r = Call(Get, FnAcType); return r != null && r.Length > 4 && r[4] == 1; }
        catch { return false; }
    }

    public bool SetMode(int mode)
    {
        try
        {
            Call(Set, FnMode, (byte)mode);
            Mode = mode;
            Changed = true;
            Log($"Modus gesetzt: {ModeName(mode)}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log($"Modus {mode} setzen fehlgeschlagen: {ex.Message}");
            return false;
        }
    }

    public bool SetBoost(bool on)
    {
        try
        {
            Call(Set, FnBoost, 0, (byte)(on ? 1 : 0));   // 0 = CPU- und GPU-Lüfter
            Boost = on;
            Changed = true;
            Log($"Lüfter-Boost {(on ? "an" : "aus")}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log($"Boost setzen fehlgeschlagen: {ex.Message}");
            return false;
        }
    }

    /// Beim Beenden / BIOS-Profil: Boost aus, ursprünglichen Modus zurück
    public void Restore()
    {
        if (!Changed) return;
        if (Boost) SetBoost(false);
        if (OriginalMode is int m && Mode != m) SetMode(m);
        Changed = false;
    }
}
