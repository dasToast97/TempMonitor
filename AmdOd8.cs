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

    /// Für eine AMD-Grafikkarte mit Overdrive 8 und Lüfterkurve – sonst null
    public static AmdOd8Fan? TryCreate(IHardware gpu)
    {
        if (gpu.HardwareType != HardwareType.GpuAmd) return null;
        // Kennung der Bibliothek: "/gpu-amd/<ADL-Adapter-Nummer>"
        string last = gpu.Identifier.ToString().TrimEnd('/').Split('/').Last();
        if (!int.TryParse(last, out int adapter)) return null;

        IntPtr ctx = IntPtr.Zero;
        try
        {
            if (ADL2_Main_Control_Create(Alloc, 1, ref ctx) != ADL_OK || ctx == IntPtr.Zero) return null;

            int supported = 0, enabled = 0, version = 0;
            if (ADL2_Overdrive_Caps(ctx, adapter, ref supported, ref enabled, ref version) != ADL_OK || version != 8)
            {
                ADL2_Main_Control_Destroy(ctx);
                return null;
            }

            // Grenzen + Fähigkeiten
            int caps = 0, n = 0;
            IntPtr list = IntPtr.Zero;
            if (ADL2_Overdrive8_Init_SettingX2_Get(ctx, adapter, ref caps, ref n, ref list) != ADL_OK || list == IntPtr.Zero || n <= OD8_FAN_CURVE_SPEED_5)
            {
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
                ADL2_Main_Control_Destroy(ctx);
                return null;
            }
            var min = new int[n];
            var max = new int[n];
            for (int i = 0; i < n; i++)
            {
                // ADLOD8SingleInitSetting { featureID, minValue, maxValue, defaultValue } = 16 Byte
                IntPtr p = list + i * 16;
                min[i] = Marshal.ReadInt32(p, 4);
                max[i] = Marshal.ReadInt32(p, 8);
            }
            Marshal.FreeHGlobal(list);

            if ((caps & ADL_OD8_FAN_CURVE) == 0)
            {
                ADL2_Main_Control_Destroy(ctx);
                return null;
            }

            var current = ReadCurrent(ctx, adapter);
            if (current == null || current.Length <= OD8_FAN_CURVE_SPEED_5)
            {
                ADL2_Main_Control_Destroy(ctx);
                return null;
            }

            int autoDefault = 1;
            try { ADL2_Adapter_RegValueInt_Get(ctx, adapter, 1, null, "IsAutoDefault", ref autoDefault); } catch { }

            return new AmdOd8Fan(ctx, adapter, caps, min, max, current, autoDefault);
        }
        catch
        {
            // atiadlxx.dll fehlt (kein AMD-Treiber) oder Funktion unbekannt
            try { if (ctx != IntPtr.Zero) ADL2_Main_Control_Destroy(ctx); } catch { }
            return null;
        }
    }

    static int[]? ReadCurrent(IntPtr ctx, int adapter)
    {
        int n = 0;
        IntPtr list = IntPtr.Zero;
        if (ADL2_Overdrive8_Current_SettingX2_Get(ctx, adapter, ref n, ref list) != ADL_OK || list == IntPtr.Zero)
        {
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            return null;
        }
        var r = new int[n];
        Marshal.Copy(list, r, 0, n);
        Marshal.FreeHGlobal(list);
        return r;
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

    /// Lüfter auf einen festen Prozentwert stellen (flache Kurve)
    public bool SetPercent(float pct)
    {
        lock (_lock)
        {
            if (_ctx == IntPtr.Zero) return false;
            // Nicht jede Sekunde neu schreiben: nur bei spürbarer Änderung oder alle 10 s
            bool big = _lastPct < 0 || Math.Abs(pct - _lastPct) >= 2 || (pct >= 99 && _lastPct < 99);
            if (!big && (DateTime.Now - _lastWrite).TotalSeconds < 10) return true;

            // 1) Wie bei AMD: Tuning-Modus auf "Manuell", sonst werden Lüfterwerte ignoriert
            if (!_changed && (_caps & ADL_OD8_OPTIMIZED_GPU_POWER_MODE) != 0
                && OD8_OPTIMZED_POWER_MODE < _original.Length && _original[OD8_OPTIMZED_POWER_MODE] != POWER_MODE_MANUAL)
            {
                if (!Apply(new[] { (OD8_OPTIMZED_POWER_MODE, POWER_MODE_MANUAL) })) return false;
            }

            // 2) Kurve: Punkte 1–4 = gewünschte Leistung, Punkt 5 = 100 % bei ca. 90 °C.
            //    Der letzte Punkt ist eine Rückfallebene im Treiber: Wird Toast Temperatur hart beendet,
            //    dreht die Karte bei Hitze trotzdem von selbst hoch.
            int t5 = Clamp(OD8_FAN_CURVE_TEMPERATURE_1 + 8, 95);
            int t4 = Clamp(OD8_FAN_CURVE_TEMPERATURE_1 + 6, Math.Min(t5 - 5, 90));
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
            // Zero-RPM aus, damit auch kleine Werte wirklich drehen
            if ((_caps & ADL_OD8_FAN_ZERO_RPM_CONTROL) != 0)
                items.Add((OD8_FAN_ZERORPM_CONTROL, 0));

            if (!Apply(items)) return false;

            if (!_changed)
            {
                try { ADL2_Adapter_RegValueInt_Set(_ctx, _adapter, 1, null, "IsAutoDefault", 0); } catch { }
            }
            _changed = true;
            _lastPct = pct;
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
