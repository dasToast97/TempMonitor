using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

// ===========================================================================
// Lüftersteuerung für AMD-Grafikkarten ab RX 6000 (RDNA2+) über AMD Overdrive 8.
//
// Die Bibliothek LibreHardwareMonitor nutzt für AMD-Lüfter die alte Schnittstelle
// Overdrive 5, die der Treiber ab RX 6000 ignoriert. Overdrive 8 kennt keinen festen
// Prozentwert, sondern eine Lüfterkurve mit 5 Punkten. Für eine gewünschte Leistung
// werden alle 5 Punkte auf diesen Wert gesetzt (flache Kurve).
//
// Umsetzung streng nach dem offiziellen AMD-Beispiel (display-library, Sample/Overdrive8):
//  - Grenzen werden von der Karte gelesen und eingehalten
//  - Es werden nur Lüfterwerte (Kurve, Zero-RPM) und der Tuning-Modus gesetzt – nie Takt/Spannung
//  - Beim Beenden werden die ursprünglichen Werte exakt zurückgeschrieben
// ===========================================================================
sealed class AmdOd8Fan : IDisposable
{
    const string Dll = "atiadlxx.dll";
    const int ADL_OK = 0;
    const int OD8_COUNT = 77;   // Größe der Tabelle laut aktuellem SDK

    // Kennungen aus adl_defines.h (ADLOD8SettingId)
    const int OD8_FAN_ZERORPM_CONTROL = 15;
    const int OD8_FAN_CURVE_TEMPERATURE_1 = 19;
    const int OD8_FAN_CURVE_SPEED_5 = 28;
    const int OD8_OPTIMZED_POWER_MODE = 36;

    // Fähigkeiten (ADLOD8FeatureControl)
    const int ADL_OD8_FAN_ZERO_RPM_CONTROL = 1 << 9;
    const int ADL_OD8_FAN_CURVE = 1 << 13;
    const int ADL_OD8_OPTIMIZED_GPU_POWER_MODE = 1 << 16;

    const int POWER_MODE_MANUAL = 3;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr AllocDelegate(int size);
    static readonly AllocDelegate Alloc = Marshal.AllocHGlobal;   // Feld hält den Delegaten am Leben

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Main_Control_Create(AllocDelegate callback, int enumConnectedAdapters, ref IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Overdrive_Caps(IntPtr context, int adapterIndex, ref int supported, ref int enabled, ref int version);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Overdrive8_Init_SettingX2_Get(IntPtr context, int adapterIndex, ref int capabilities, ref int numberOfFeatures, ref IntPtr initSettingList);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Overdrive8_Current_SettingX2_Get(IntPtr context, int adapterIndex, ref int numberOfFeatures, ref IntPtr currentSettingList);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Overdrive8_Init_Setting_Get(IntPtr context, int adapterIndex, IntPtr initSetting);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Overdrive8_Current_Setting_Get(IntPtr context, int adapterIndex, IntPtr currentSetting);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Overdrive8_Setting_Set(IntPtr context, int adapterIndex, IntPtr setSetting, IntPtr currentSetting);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    static extern int ADL2_Adapter_RegValueInt_Get(IntPtr context, int adapterIndex, int driverPathOption, string? subKey, string keyName, ref int value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    static extern int ADL2_Adapter_RegValueInt_Set(IntPtr context, int adapterIndex, int driverPathOption, string? subKey, string keyName, int value);

    readonly object _lock = new();
    IntPtr _ctx;
    readonly int _adapter;
    readonly int _caps;
    readonly int[] _min, _max;
    readonly int[] _original;          // Einstellungen beim Start (zum Zurückschreiben)
    readonly int _originalAutoDefault;
    bool _changed;
    float _lastPct = -1;
    DateTime _lastWrite = DateTime.MinValue;

    public string? LastError { get; private set; }

    AmdOd8Fan(IntPtr ctx, int adapter, int caps, int[] min, int[] max, int[] original, int autoDefault)
    {
        _ctx = ctx;
        _adapter = adapter;
        _caps = caps;
        _min = min;
        _max = max;
        _original = original;
        _originalAutoDefault = autoDefault;
    }

    // ---------- Adapter-Liste (für die Suche nach dem richtigen Adapter) ----------
    const int ADL_MAX_PATH = 256;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct ADLAdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string UDID;
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DisplayName;
        public int Present;
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string PNPString;
        public int OSDisplayIndex;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, ref int numAdapters);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int size);

    /// Ergebnis der letzten Einrichtung (für Anzeige und Diagnosedatei)
    public static string LastInitInfo { get; private set; } = "";
    public static readonly string LogFile = Path.Combine(
        AppPaths.Config, "amd-od8.log");

    /// Für eine AMD-Grafikkarte mit Overdrive 8 und Lüfterkurve – sonst null (Grund in LastInitInfo)
    public static AmdOd8Fan? TryCreate(IHardware gpu)
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine($"Toast Temperatur {Updater.CurrentVersion} · {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        log.AppendLine($"GPU: {gpu.Name}  ({gpu.Identifier})");
        string reason = "unbekannt";
        AmdOd8Fan? result = null;
        IntPtr ctx = IntPtr.Zero;

        try
        {
            if (gpu.HardwareType != HardwareType.GpuAmd) { reason = "keine AMD-Grafikkarte"; return null; }

            int rc = ADL2_Main_Control_Create(Alloc, 1, ref ctx);
            log.AppendLine($"ADL2_Main_Control_Create = {rc}");
            if (rc != ADL_OK || ctx == IntPtr.Zero) { reason = $"AMD-Schnittstelle startet nicht (Code {rc})"; return null; }

            // Kandidaten: zuerst die Nummer der Bibliothek, dann alle Adapter mit passendem Namen
            var candidates = new List<int>();
            string last = gpu.Identifier.ToString().TrimEnd('/').Split('/').Last();
            if (int.TryParse(last, out int libIndex)) candidates.Add(libIndex);

            int num = 0;
            if (ADL2_Adapter_NumberOfAdapters_Get(ctx, ref num) == ADL_OK && num > 0 && num < 512)
            {
                int el = Marshal.SizeOf<ADLAdapterInfo>();
                IntPtr buf = Marshal.AllocHGlobal(el * num);
                try
                {
                    for (int i = 0; i < el * num; i += 4) Marshal.WriteInt32(buf, i, 0);
                    if (ADL2_Adapter_AdapterInfo_Get(ctx, buf, el * num) == ADL_OK)
                    {
                        var same = new List<int>();
                        var other = new List<int>();
                        for (int i = 0; i < num; i++)
                        {
                            var info = Marshal.PtrToStructure<ADLAdapterInfo>(buf + i * el);
                            log.AppendLine($"  Adapter {info.AdapterIndex}: \"{info.AdapterName?.Trim()}\" Bus {info.BusNumber} Present {info.Present}");
                            if (info.BusNumber < 0) continue;
                            bool nameMatch = !string.IsNullOrWhiteSpace(info.AdapterName)
                                             && string.Equals(info.AdapterName.Trim(), gpu.Name.Trim(), StringComparison.OrdinalIgnoreCase);
                            (nameMatch ? same : other).Add(info.AdapterIndex);
                        }
                        candidates.AddRange(same);
                        candidates.AddRange(other);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            candidates = candidates.Distinct().ToList();
            log.AppendLine($"Kandidaten: {string.Join(", ", candidates)}");

            foreach (int adapter in candidates)
            {
                var r = Probe(ctx, adapter, log, out string why);
                if (r != null) { result = r; reason = $"aktiv (Adapter {adapter})"; return result; }
                reason = why;
            }
            return null;
        }
        catch (Exception ex)
        {
            reason = ex is DllNotFoundException ? "AMD-Treiber-Bibliothek nicht gefunden" : ex.GetType().Name + ": " + ex.Message;
            log.AppendLine("Fehler: " + ex);
            return null;
        }
        finally
        {
            if (result == null && ctx != IntPtr.Zero) { try { ADL2_Main_Control_Destroy(ctx); } catch { } }
            LastInitInfo = reason;
            log.AppendLine("Ergebnis: " + reason);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.WriteAllText(LogFile, log.ToString());
            }
            catch { }
        }
    }

    /// Prüft einen Adapter Schritt für Schritt; bei Erfolg gibt es die fertige Steuerung zurück
    static AmdOd8Fan? Probe(IntPtr ctx, int adapter, System.Text.StringBuilder log, out string why)
    {
        int supported = 0, enabled = 0, version = 0;
        int rc = ADL2_Overdrive_Caps(ctx, adapter, ref supported, ref enabled, ref version);
        log.AppendLine($"[{adapter}] Overdrive_Caps = {rc}, supported {supported}, enabled {enabled}, version {version}");
        if (rc != ADL_OK) { why = $"Overdrive-Abfrage fehlgeschlagen (Code {rc})"; return null; }
        if (version != 8) { why = $"Overdrive-Version {version} statt 8"; return null; }

        int caps = 0;
        int[]? min = null, max = null;

        // a) neuere Abfrage – die Anzahl muss wie im AMD-Beispiel vorbelegt sein
        int n = OD8_COUNT;
        IntPtr list = IntPtr.Zero;
        try { rc = ADL2_Overdrive8_Init_SettingX2_Get(ctx, adapter, ref caps, ref n, ref list); }
        catch (EntryPointNotFoundException) { rc = -8; }
        log.AppendLine($"[{adapter}] Init_SettingX2 = {rc}, Merkmale {n}, Fähigkeiten 0x{caps:X}");
        if (rc == ADL_OK && list != IntPtr.Zero && n > OD8_FAN_CURVE_SPEED_5)
        {
            min = new int[n];
            max = new int[n];
            for (int i = 0; i < n; i++)
            {
                // ADLOD8SingleInitSetting { featureID, minValue, maxValue, defaultValue } = 16 Byte
                IntPtr p = list + i * 16;
                min[i] = Marshal.ReadInt32(p, 4);
                max[i] = Marshal.ReadInt32(p, 8);
            }
        }
        if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);

        // b) Ausweichweg: ältere Abfrage mit fester Tabelle
        if (min == null)
        {
            // ADLOD8InitSetting { int count; int capabilities; ADLOD8SingleInitSetting[OD8_COUNT] }
            int size = 8 + OD8_COUNT * 16;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i += 4) Marshal.WriteInt32(buf, i, 0);
                Marshal.WriteInt32(buf, 0, OD8_COUNT);
                try { rc = ADL2_Overdrive8_Init_Setting_Get(ctx, adapter, buf); }
                catch (EntryPointNotFoundException) { rc = -8; }
                int cnt = Math.Min(Marshal.ReadInt32(buf, 0), OD8_COUNT);
                log.AppendLine($"[{adapter}] Init_Setting (alt) = {rc}, Merkmale {cnt}, Fähigkeiten 0x{Marshal.ReadInt32(buf, 4):X}");
                if (rc == ADL_OK && cnt > OD8_FAN_CURVE_SPEED_5)
                {
                    caps = Marshal.ReadInt32(buf, 4);
                    min = new int[cnt];
                    max = new int[cnt];
                    for (int i = 0; i < cnt; i++)
                    {
                        IntPtr p = buf + 8 + i * 16;
                        min[i] = Marshal.ReadInt32(p, 4);
                        max[i] = Marshal.ReadInt32(p, 8);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        if (min == null || max == null)
        {
            why = $"Overdrive-8-Grenzwerte nicht lesbar (Code {rc})";
            return null;
        }
        log.AppendLine($"[{adapter}] Kurve Temp {min[OD8_FAN_CURVE_TEMPERATURE_1]}–{max[OD8_FAN_CURVE_TEMPERATURE_1]} °C, Drehzahl {min[OD8_FAN_CURVE_TEMPERATURE_1 + 1]}–{max[OD8_FAN_CURVE_TEMPERATURE_1 + 1]} %");

        if ((caps & ADL_OD8_FAN_CURVE) == 0) { why = "Karte meldet keine Lüfterkurve (Overdrive 8)"; return null; }

        var current = ReadCurrent(ctx, adapter);
        log.AppendLine($"[{adapter}] Current_SettingX2: {(current == null ? "fehlgeschlagen" : current.Length + " Werte")}");
        if (current == null || current.Length <= OD8_FAN_CURVE_SPEED_5) { why = "aktuelle Overdrive-8-Werte nicht lesbar"; return null; }
        log.AppendLine($"[{adapter}] Kurve jetzt: " + string.Join(" | ", Enumerable.Range(0, 5).Select(i =>
            $"{current[OD8_FAN_CURVE_TEMPERATURE_1 + i * 2]}°→{current[OD8_FAN_CURVE_TEMPERATURE_1 + i * 2 + 1]}%")));

        int autoDefault = 1;
        try { ADL2_Adapter_RegValueInt_Get(ctx, adapter, 1, null, "IsAutoDefault", ref autoDefault); } catch { }

        why = "ok";
        return new AmdOd8Fan(ctx, adapter, caps, min, max, current, autoDefault);
    }

    static int[]? ReadCurrent(IntPtr ctx, int adapter)
    {
        // a) neuere Abfrage (Anzahl vorbelegt wie im AMD-Beispiel)
        int n = OD8_COUNT;
        IntPtr list = IntPtr.Zero;
        int rc;
        try { rc = ADL2_Overdrive8_Current_SettingX2_Get(ctx, adapter, ref n, ref list); }
        catch (EntryPointNotFoundException) { rc = -8; }
        if (rc == ADL_OK && list != IntPtr.Zero && n > 0)
        {
            var r = new int[n];
            Marshal.Copy(list, r, 0, n);
            Marshal.FreeHGlobal(list);
            return r;
        }
        if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);

        // b) Ausweichweg: ADLOD8CurrentSetting { int count; int[OD8_COUNT] }
        int size = 4 + OD8_COUNT * 4;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(buf, i, 0);
            Marshal.WriteInt32(buf, 0, OD8_COUNT);
            try { rc = ADL2_Overdrive8_Current_Setting_Get(ctx, adapter, buf); }
            catch (EntryPointNotFoundException) { return null; }
            if (rc != ADL_OK) return null;
            int cnt = Math.Clamp(Marshal.ReadInt32(buf, 0), 0, OD8_COUNT);
            var r = new int[cnt];
            Marshal.Copy(buf + 4, r, 0, cnt);
            return r;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// Ein Satz Änderungen: (Kennung, Wert)
    bool Apply(IEnumerable<(int Id, int Value)> items)
    {
        // ADLOD8SetSetting { int count; ADLOD8SingleSetSetting { value, requested, reset }[OD8_COUNT] }
        // ADLOD8CurrentSetting { int count; int[OD8_COUNT] }
        int size = Math.Max(OD8_COUNT, _min.Length);
        IntPtr set = Marshal.AllocHGlobal(4 + size * 12);
        IntPtr cur = Marshal.AllocHGlobal(4 + size * 4);
        try
        {
            for (int i = 0; i < 4 + size * 12; i += 4) Marshal.WriteInt32(set, i, 0);
            for (int i = 0; i < 4 + size * 4; i += 4) Marshal.WriteInt32(cur, i, 0);
            Marshal.WriteInt32(set, 0, OD8_COUNT);
            Marshal.WriteInt32(cur, 0, OD8_COUNT);
            foreach (var (id, value) in items)
            {
                IntPtr e = set + 4 + id * 12;
                Marshal.WriteInt32(e, 0, value);   // value
                Marshal.WriteInt32(e, 4, 1);       // requested
                Marshal.WriteInt32(e, 8, 0);       // reset = nein
            }
            int r = ADL2_Overdrive8_Setting_Set(_ctx, _adapter, set, cur);
            LastError = r == ADL_OK ? null : $"AMD-Treiber meldet Fehler {r}";
            if (r != ADL_OK)
            {
                try
                {
                    File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss} Setting_Set [{_adapter}] = {r} für " +
                        string.Join(", ", items.Select(i => $"{i.Id}={i.Value}")) + Environment.NewLine);
                }
                catch { }
            }
            return r == ADL_OK;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(set);
            Marshal.FreeHGlobal(cur);
        }
    }

    int Clamp(int id, int v) => id < _min.Length && _max[id] > _min[id] ? Math.Clamp(v, _min[id], _max[id]) : v;

    /// Kann der Treiber die Lüfter im Leerlauf anhalten (Zero RPM)?
    public bool CanZeroRpm => (_caps & ADL_OD8_FAN_ZERO_RPM_CONTROL) != 0;
    bool _lastZero;

    /// Lüfter auf einen festen Prozentwert stellen (flache Kurve).
    /// 0 % = Leerlauf: Zero RPM einschalten, der Treiber hält die Lüfter an, solange die Karte kühl ist.
    public bool SetPercent(float pct)
    {
        bool zero = pct <= 0 && CanZeroRpm;
        lock (_lock)
        {
            if (_ctx == IntPtr.Zero) return false;
            // Nicht jede Sekunde neu schreiben: nur bei spürbarer Änderung oder alle 10 s
            bool big = _lastPct < 0 || Math.Abs(pct - _lastPct) >= 2 || (pct >= 99 && _lastPct < 99) || zero != _lastZero;
            if (!big && (DateTime.Now - _lastWrite).TotalSeconds < 10) return true;

            // 1) Wie bei AMD: Tuning-Modus auf "Manuell", sonst werden Lüfterwerte ignoriert
            if (!_changed && (_caps & ADL_OD8_OPTIMIZED_GPU_POWER_MODE) != 0
                && OD8_OPTIMZED_POWER_MODE < _original.Length && _original[OD8_OPTIMZED_POWER_MODE] != POWER_MODE_MANUAL)
            {
                if (!Apply(new[] { (OD8_OPTIMZED_POWER_MODE, POWER_MODE_MANUAL) })) return false;
            }

            // 2) Kurve: Punkte 1–4 = gewünschte Leistung, Punkt 5 = 100 % am oberen Ende (ab ca. 95 °C).
            //    Der letzte Punkt ist eine Rückfallebene im Treiber: Wird Toast Temperatur hart beendet,
            //    dreht die Karte bei Hitze trotzdem von selbst hoch.
            // Rückfallebene erst ab ca. 95 °C Hot Spot, damit sie den normalen Betrieb nicht mitregelt
            int tMax = OD8_FAN_CURVE_TEMPERATURE_1 + 8 < _max.Length && _max[OD8_FAN_CURVE_TEMPERATURE_1 + 8] > 0
                ? _max[OD8_FAN_CURVE_TEMPERATURE_1 + 8] : 100;
            int t5 = Clamp(OD8_FAN_CURVE_TEMPERATURE_1 + 8, Math.Min(tMax, 100));
            int t4 = Clamp(OD8_FAN_CURVE_TEMPERATURE_1 + 6, Math.Max(t5 - 5, Math.Min(95, t5 - 1)));
            int t3 = Clamp(OD8_FAN_CURVE_TEMPERATURE_1 + 4, Math.Min(_original[OD8_FAN_CURVE_TEMPERATURE_1 + 4], t4 - 5));
            int t2 = Clamp(OD8_FAN_CURVE_TEMPERATURE_1 + 2, Math.Min(_original[OD8_FAN_CURVE_TEMPERATURE_1 + 2], t3 - 5));
            int t1 = Clamp(OD8_FAN_CURVE_TEMPERATURE_1, Math.Min(_original[OD8_FAN_CURVE_TEMPERATURE_1], t2 - 5));
            int[] temps = { t1, t2, t3, t4, t5 };
            int speed = (int)Math.Round(pct);
            var items = new List<(int, int)>();
            for (int i = 0; i < 5; i++)
            {
                int tid = OD8_FAN_CURVE_TEMPERATURE_1 + i * 2;
                items.Add((tid, temps[i]));
                int sp = i == 4 ? Math.Max(speed, 100) : speed;
                items.Add((tid + 1, Clamp(tid + 1, sp)));
            }
            // Zero-RPM: im Leerlauf an (Lüfter dürfen stehen), sonst aus, damit auch kleine Werte wirklich drehen
            if ((_caps & ADL_OD8_FAN_ZERO_RPM_CONTROL) != 0)
                items.Add((OD8_FAN_ZERORPM_CONTROL, zero ? 1 : 0));

            if (!Apply(items)) return false;

            if (!_changed)
            {
                try { ADL2_Adapter_RegValueInt_Set(_ctx, _adapter, 1, null, "IsAutoDefault", 0); } catch { }
            }
            _changed = true;
            _lastPct = pct;
            _lastZero = zero;
            _lastWrite = DateTime.Now;
            return true;
        }
    }

    /// Ursprüngliche Lüfterkurve, Zero-RPM und Tuning-Modus zurückschreiben
    public void Restore()
    {
        lock (_lock)
        {
            if (_ctx == IntPtr.Zero || !_changed) return;
            var items = new List<(int, int)>();
            for (int id = OD8_FAN_CURVE_TEMPERATURE_1; id <= OD8_FAN_CURVE_SPEED_5; id++)
                items.Add((id, _original[id]));
            if ((_caps & ADL_OD8_FAN_ZERO_RPM_CONTROL) != 0)
                items.Add((OD8_FAN_ZERORPM_CONTROL, _original[OD8_FAN_ZERORPM_CONTROL]));
            Apply(items);

            if ((_caps & ADL_OD8_OPTIMIZED_GPU_POWER_MODE) != 0 && OD8_OPTIMZED_POWER_MODE < _original.Length
                && _original[OD8_OPTIMZED_POWER_MODE] != POWER_MODE_MANUAL)
                Apply(new[] { (OD8_OPTIMZED_POWER_MODE, _original[OD8_OPTIMZED_POWER_MODE]) });

            try { ADL2_Adapter_RegValueInt_Set(_ctx, _adapter, 1, null, "IsAutoDefault", _originalAutoDefault); } catch { }
            _changed = false;
            _lastPct = -1;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { Restore(); } catch { }
            try { if (_ctx != IntPtr.Zero) ADL2_Main_Control_Destroy(_ctx); } catch { }
            _ctx = IntPtr.Zero;
        }
    }
}
