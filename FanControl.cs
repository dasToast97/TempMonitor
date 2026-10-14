using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

// ===========================================================================
// Grundbegriffe
// ===========================================================================
enum FanProfile { Bios, Eco, Balanced, Performance, Max, Custom }

/// Nach welchem Bauteil ein Lüfter regelt
enum Component { Cpu = 0, Gpu = 1, Board = 2, Hotspot = 3 }

enum FanKind { Fan, Pump }

static class Components
{
    /// Alle Regelziele (inkl. GPU-Hot-Spot mit eigenem Ziel)
    public static readonly Component[] All = { Component.Cpu, Component.Gpu, Component.Board, Component.Hotspot };

    /// Was man pro Lüfter anhaken kann (Hot Spot gehört automatisch zur GPU)
    public static readonly Component[] Selectable = { Component.Cpu, Component.Gpu, Component.Board };

    public static string Name(Component c) => c switch
    {
        Component.Cpu => "CPU",
        Component.Gpu => "GPU",
        Component.Hotspot => "GPU-Hot-Spot",
        _ => "Mainboard",
    };
}

sealed record ProfileInfo(
    FanProfile Profile, string Title, string Description, bool Adaptive,
    float MinPercent, float UpRate, float DownRate, float[] DefaultTargets);

static class Profiles
{
    // UpRate / DownRate = maximale Änderung in % pro Sekunde (verhindert nerviges Auf- und Abheulen)
    // DefaultTargets = Zieltemperaturen CPU, GPU, Mainboard, GPU-Hot-Spot (einstellbar)
    public static readonly ProfileInfo[] All =
    {
        new(FanProfile.Bios,        "Standard (BIOS)",  "Mainboard und Grafikkarte regeln selbst",      false,   0,   0,   0, new float[] { 0, 0, 0, 0 }),
        new(FanProfile.Eco,         "Energiesparend",   "So leise wie möglich",                         true,   20,   4,   1, new float[] { 78, 75, 62, 90 }),
        new(FanProfile.Balanced,    "Ausgewogen",       "Ruhig und kühl",                               true,   25,   6,   2, new float[] { 68, 70, 56, 85 }),
        new(FanProfile.Performance, "Leistung",         "Kühl für Spiele & Rendern",                    true,   35,  10,   2, new float[] { 58, 62, 50, 78 }),
        new(FanProfile.Max,         "Maximale Kühlung", "Alle Lüfter dauerhaft auf 100 %",              false, 100, 100, 100, new float[] { 0, 0, 0, 0 }),
        new(FanProfile.Custom,      "Eigene Kurve",     "Für jeden Lüfter eine eigene Kurve festlegen", false,   0,   8,   3, new float[] { 0, 0, 0, 0 }),
    };

    public static ProfileInfo Get(FanProfile p) => All.First(i => i.Profile == p);
}

// ===========================================================================
// Einstellungen (werden in %AppData%\TempMonitor\fans.json gespeichert)
// ===========================================================================
sealed class FanChannelSettings
{
    public string? CustomName { get; set; }
    public FanKind? Kind { get; set; }           // null = automatisch erkennen
    public bool SourcesInitialized { get; set; }
    public bool UseCpu { get; set; }
    public bool UseGpu { get; set; }
    public bool UseBoard { get; set; }
    public List<float[]> Curve { get; set; } = DefaultCurve();
    public float Kp { get; set; } = 3.0f;
    public float Ki { get; set; } = 0.15f;
    /// Gelernte Lüfterleistung je Auslastungsstufe (0–10 %, 10–20 % …) pro Profil und Bauteil; -1 = unbekannt
    public Dictionary<string, float[]> Learned { get; set; } = new();

    public static List<float[]> DefaultCurve() => new()
    {
        new[] { 30f, 25f }, new[] { 50f, 35f }, new[] { 65f, 55f }, new[] { 75f, 75f }, new[] { 85f, 100f },
    };

    public bool Uses(Component c) => c switch
    {
        Component.Cpu => UseCpu,
        Component.Gpu or Component.Hotspot => UseGpu,
        _ => UseBoard,
    };

    public void SetUse(Component c, bool v)
    {
        switch (c)
        {
            case Component.Cpu: UseCpu = v; break;
            case Component.Gpu: UseGpu = v; break;
            default: UseBoard = v; break;
        }
    }

    public float[] LearnedFor(FanProfile p, Component c)
    {
        string key = $"{p}|{c}";
        if (!Learned.TryGetValue(key, out var t) || t.Length != 11)
        {
            t = Enumerable.Repeat(-1f, 11).ToArray();
            Learned[key] = t;
        }
        return t;
    }
}

sealed class FanSettings
{
    public FanProfile Profile { get; set; } = FanProfile.Bios;
    public bool WarningAccepted { get; set; }
    public bool TrayHintShown { get; set; }

    /// Zieltemperaturen je Profil: [CPU, GPU, Mainboard]
    public Dictionary<string, float[]> Targets { get; set; } = new();

    // Sicherheitsfunktionen – standardmäßig aus, in den Einstellungen einschaltbar
    public bool EmergencyEnabled { get; set; }
    public float CpuCritical { get; set; } = 90f;
    public float GpuCritical { get; set; } = 85f;
    public float BoardCritical { get; set; } = 80f;
    public float HotspotCritical { get; set; } = 105f;
    public bool StallDetection { get; set; }

    /// Mindestleistung für Pumpen (Wasserkühlung), damit sie nie zu langsam laufen
    public float PumpMinPercent { get; set; } = 60f;

    public Dictionary<string, FanChannelSettings> Fans { get; set; } = new();

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");
    static readonly string FilePath = Path.Combine(Dir, "fans.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static FanSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<FanSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* kaputte Datei -> Standardwerte */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* nicht kritisch */ }
    }

    public FanChannelSettings For(string id)
    {
        if (!Fans.TryGetValue(id, out var s)) { s = new(); Fans[id] = s; }
        return s;
    }

    public float[] TargetsFor(FanProfile p)
    {
        string key = p.ToString();
        var def = Profiles.Get(p).DefaultTargets;
        if (!Targets.TryGetValue(key, out var t) || t.Length < 3)
        {
            t = (float[])def.Clone();
            Targets[key] = t;
        }
        else if (t.Length < def.Length)
        {
            // ältere Einstellungen: Hot-Spot-Ziel ergänzen
            var n = (float[])def.Clone();
            Array.Copy(t, n, t.Length);
            t = n;
            Targets[key] = t;
        }
        return t;
    }

    public float Critical(Component c) => c switch
    {
        Component.Cpu => CpuCritical,
        Component.Gpu => GpuCritical,
        Component.Hotspot => HotspotCritical,
        _ => BoardCritical,
    };
}

// ===========================================================================
// Namen & ausgeblendete Anschlüsse – auch für die Wertetabellen
// ===========================================================================
static class FanNaming
{
    /// Drehzahl- und Steuer-Sensor-ID -> Anzeigename + Art
    public static readonly Dictionary<string, FanChannel> BySensor = new();
    /// Sensor-IDs von Anschlüssen ohne angeschlossenen Lüfter
    public static readonly HashSet<string> Hidden = new();

    public static string Label(ISensor s)
    {
        if (BySensor.TryGetValue(s.Identifier.ToString(), out var ch))
        {
            string icon = ch.Kind == FanKind.Pump ? "Pumpe" : "Lüfter";
            string nm = ch.DisplayName;
            return nm == s.Name ? $"{nm}  ({icon})" : $"{nm}  ({icon} · {s.Name})";
        }
        return s.Name;
    }
}

// ===========================================================================
// Ein steuerbarer Lüfter
// ===========================================================================
sealed class SourceState
{
    public float? Smooth;
    public float Integral;
    public int LastBucket = -1;
    public int SteadyTicks;
    public int FarTicks;
    public readonly Queue<int> Signs = new();
}

sealed class FanChannel
{
    public required IControl Control { get; init; }
    public required ISensor ControlSensor { get; init; }
    public ISensor? Rpm { get; init; }
    public required string Id { get; init; }
    public required string DefaultName { get; init; }
    public required string HardwareName { get; init; }
    public bool IsGpu { get; init; }
    public required FanChannelSettings Cfg { get; init; }
    /// AMD RX 6000+: Steuerung über Overdrive 8 statt über die Bibliothek
    public AmdOd8Fan? Od8 { get; init; }
    /// Warum Overdrive 8 nicht eingerichtet werden konnte (nur AMD)
    public string? Od8Note { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Cfg.CustomName) ? DefaultName : Cfg.CustomName!;

    public FanKind AutoKind =>
        DefaultName.Contains("pump", StringComparison.OrdinalIgnoreCase)
        || DefaultName.Contains("aio", StringComparison.OrdinalIgnoreCase)
            ? FanKind.Pump : FanKind.Fan;

    public FanKind Kind => Cfg.Kind ?? AutoKind;

    // Laufzeit-Zustand
    public float Output = -1;
    public bool Faulted;
    public int NoSpinTicks;
    public string Status = "";
    public DateTime IdentifyUntil = DateTime.MinValue;
    public bool Identifying;
    public Component? Driver;          // Bauteil, das gerade die Drehzahl bestimmt

    // Erfolgskontrolle: reagiert der Lüfter überhaupt auf unsere Befehle?
    public bool NotControllable;
    public int TestFailures;
    public DateTime? TestStart;
    public float TestFromPct, TestToPct, TestStartRpm;
    public float LastTestedPct = -1;
    public float IdentifyStartRpm = -1;
    public int ReadbackMismatchTicks;
    public float? DriverTemp;
    public readonly Dictionary<Component, SourceState> States = new()
    {
        [Component.Cpu] = new(), [Component.Gpu] = new(), [Component.Board] = new(), [Component.Hotspot] = new(),
    };

    public void ResetStates()
    {
        foreach (var c in Components.All) States[c] = new SourceState();
        Driver = null;
    }
}

// ===========================================================================
// Die eigentliche Steuerung
// ===========================================================================
sealed class FanController
{
    readonly object _lock = new();
    readonly FanSettings _s;
    DateTime _lastSave = DateTime.Now;

    public List<FanChannel> Channels { get; } = new();
    public int HiddenCount { get; private set; }
    public FanSettings Settings => _s;
    public FanProfile Profile => _s.Profile;

    readonly Dictionary<Component, ISensor?> _temp = new();
    ISensor? _gpuHotspot;


    ISensor? _cpuLoad, _gpuLoad;

    /// Die laufende Steuerung (für die Lüfter-Kachel in der Übersicht)
    public static FanController? Current { get; set; }

    public FanController(FanSettings settings) => _s = settings;

    // ---------------------------------------------------------------------
    // Hauptwerte je Bauteil
    // ---------------------------------------------------------------------
    public ISensor? TempSensor(Component c) => _temp.TryGetValue(c, out var s) ? s : null;

    public float? Temp(Component c) =>
        TempSensor(c)?.Value is float v && v > 1 && v < 125 ? v : null;

    public float? Load(Component c) => c switch
    {
        Component.Cpu => _cpuLoad?.Value,
        Component.Gpu or Component.Hotspot => _gpuLoad?.Value,
        // Mainboard wird von CPU und GPU aufgeheizt -> die höhere Last zählt
        _ => Math.Max(_cpuLoad?.Value ?? 0, _gpuLoad?.Value ?? 0),
    };

    public bool HasComponent(Component c) => TempSensor(c) != null;

    public float? GpuHotspot => _gpuHotspot?.Value;

    // ---------------------------------------------------------------------
    // Lüfter suchen
    // ---------------------------------------------------------------------
    static bool IsGpu(HardwareType t) =>
        t is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    public void Discover(IComputer computer)
    {
        Channels.Clear();
        FanNaming.BySensor.Clear();
        FanNaming.Hidden.Clear();
        HiddenCount = 0;

        foreach (var hw in computer.Hardware)
        {
            var temps = MainForm.AllSensors(hw).Where(MainForm.IsUsefulTemp).ToList();
            var all = MainForm.AllSensors(hw).ToList();

            if (hw.HardwareType == HardwareType.Cpu && !_temp.ContainsKey(Component.Cpu))
            {
                if (temps.Count > 0) _temp[Component.Cpu] = MainForm.PickMain(hw, temps);
                _cpuLoad = MainForm.MainLoad(hw);
            }
            else if (IsGpu(hw.HardwareType) && !_temp.ContainsKey(Component.Gpu))
            {
                if (temps.Count > 0) _temp[Component.Gpu] = MainForm.PickMain(hw, temps);
                _gpuLoad = MainForm.MainLoad(hw);
                _gpuHotspot = temps.FirstOrDefault(t => t.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                                                     || t.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase)
                                                     || t.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase));
                if (_gpuHotspot != null) _temp[Component.Hotspot] = _gpuHotspot;
            }
            else if (hw.HardwareType == HardwareType.Motherboard && !_temp.ContainsKey(Component.Board) && temps.Count > 0)
            {
                _temp[Component.Board] = MainForm.PickMain(hw, temps);
            }
        }

        foreach (var hw in computer.Hardware)
            AddChannels(hw, hw);
    }

    void AddChannels(IHardware top, IHardware hw)
    {
        bool gpu = IsGpu(top.HardwareType);
        var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();

        foreach (var sensor in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null))
        {
            string baseName = sensor.Name.Replace("Control", "", StringComparison.OrdinalIgnoreCase).Replace("  ", " ").Trim();
            var rpm = fans.FirstOrDefault(f => f.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                      ?? (gpu ? fans.FirstOrDefault() : null);

            // Nicht angeschlossen: Mainboard-Anschluss ohne Drehzahl.
            // GPU-Lüfter bleiben immer, da sie im Leerlauf oft absichtlich stehen.
            bool connected = gpu || (rpm?.Value ?? 0) > 0;
            if (!connected)
            {
                HiddenCount++;
                FanNaming.Hidden.Add(sensor.Identifier.ToString());
                if (rpm != null) FanNaming.Hidden.Add(rpm.Identifier.ToString());
                continue;
            }

            string id = sensor.Control.Identifier.ToString();
            var cfg = _s.For(id);

            string defName = gpu
                ? (fans.Count > 1 && rpm != null ? $"Grafikkarte · {rpm.Name}" : "Grafikkarten-Lüfter")
                : FriendlyName(rpm?.Name ?? baseName);

            // AMD-Grafikkarten ab RX 6000: eigener Weg über Overdrive 8
            AmdOd8Fan? od8 = null;
            string? od8Note = null;
            if (top.HardwareType == HardwareType.GpuAmd && !Channels.Any(c => c.Od8 != null && c.HardwareName == top.Name))
            {
                try { od8 = AmdOd8Fan.TryCreate(top); } catch { od8 = null; }
                od8Note = AmdOd8Fan.LastInitInfo;
            }

            var ch = new FanChannel
            {
                Od8 = od8,
                Od8Note = od8 == null ? od8Note : null,
                Control = sensor.Control,
                ControlSensor = sensor,
                Rpm = rpm,
                Id = id,
                DefaultName = defName,
                HardwareName = top.Name,
                IsGpu = gpu,
                Cfg = cfg,
            };

            // Lüfter der Grafikkarte kühlen nur die Grafikkarte
            if (gpu)
            {
                cfg.UseGpu = true;
                cfg.UseCpu = cfg.UseBoard = false;
                cfg.SourcesInitialized = true;
            }
            if (!cfg.SourcesInitialized)
            {
                if (gpu) cfg.UseGpu = true;
                else if (ch.Kind == FanKind.Pump || defName.Contains("CPU", StringComparison.OrdinalIgnoreCase)) cfg.UseCpu = true;
                else cfg.UseCpu = cfg.UseGpu = cfg.UseBoard = true;   // Gehäuselüfter: kühlen alles
                cfg.SourcesInitialized = true;
            }

            Channels.Add(ch);
            FanNaming.BySensor[sensor.Identifier.ToString()] = ch;
            if (rpm != null) FanNaming.BySensor[rpm.Identifier.ToString()] = ch;
        }
        foreach (var sub in hw.SubHardware) AddChannels(top, sub);
    }

    /// "Fan #2" -> "Mainboard-Anschluss 2"; vom Mainboard benannte Anschlüsse (z. B. "CPU Fan") bleiben
    static string FriendlyName(string n)
    {
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^Fan\s*#?(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? $"Mainboard-Anschluss {m.Groups[1].Value}" : n;
    }

    // ---------------------------------------------------------------------
    // Profil wechseln / Erkennen / Speichern
    // ---------------------------------------------------------------------
    public void SetProfile(FanProfile p)
    {
        lock (_lock)
        {
            _s.Profile = p;
            foreach (var ch in Channels)
            {
                ch.Faulted = false;
                ch.NoSpinTicks = 0;
                ch.TestStart = null;
                ch.LastTestedPct = -1;
                ch.ReadbackMismatchTicks = 0;
                ch.ResetStates();
                if (p == FanProfile.Bios) SafeDefault(ch);
            }
            _s.Save();
        }
    }

    /// Lässt einen Lüfter 5 Sekunden auf 100 % laufen, damit man ihn zuordnen kann
    public void Identify(FanChannel ch)
    {
        lock (_lock)
        {
            ch.IdentifyUntil = DateTime.Now.AddSeconds(8);
            ch.IdentifyStartRpm = ch.Rpm?.Value ?? -1;
            ch.NotControllable = false;
            ch.TestFailures = 0;
            ch.TestStart = null;
            ch.Faulted = false;
        }
    }

    public void ResetLearning(FanChannel ch)
    {
        lock (_lock)
        {
            ch.Cfg.Learned.Clear();
            ch.Cfg.Kp = 3.0f;
            ch.Cfg.Ki = 0.15f;
            ch.ResetStates();
            _s.Save();
        }
    }

    public void Save() { lock (_lock) _s.Save(); }

    /// Alle Lüfter zurück an BIOS / Treiber. Wird beim Beenden und bei Fehlern aufgerufen.
    public void RestoreAll()
    {
        lock (_lock)
        {
            foreach (var ch in Channels) SafeDefault(ch);
            _s.Save();
        }
    }

    static void SafeDefault(FanChannel ch)
    {
        if (ch.Od8 != null) { try { ch.Od8.Restore(); } catch { } }
        try { ch.Control.SetDefault(); } catch { }
        ch.Output = -1;
    }

    /// Drehzahl setzen – bei AMD RX 6000+ über Overdrive 8, sonst über die Bibliothek
    static void Write(FanChannel ch, float pct)
    {
        if (ch.Od8 != null) ch.Od8.SetPercent(pct);
        else ch.Control.SetSoftware(pct);
    }

    // ---------------------------------------------------------------------
    // Einmal pro Sekunde (nach dem Sensor-Update)
    // ---------------------------------------------------------------------
    public void Tick()
    {
        lock (_lock)
        {
            var prof = Profiles.Get(_s.Profile);
            foreach (var ch in Channels)
            {
                try { TickChannel(ch, prof); }
                catch (Exception ex)
                {
                    SafeDefault(ch);
                    ch.Faulted = true;
                    ch.Status = "Fehler – BIOS übernimmt: " + ex.Message;
                }
            }

            if ((DateTime.Now - _lastSave).TotalSeconds > 60)
            {
                _s.Save();
                _lastSave = DateTime.Now;
            }
        }
    }

    void TickChannel(FanChannel ch, ProfileInfo prof)
    {
        // ----- Erkennen: 5 Sekunden volle Drehzahl -----
        if (DateTime.Now < ch.IdentifyUntil)
        {
            ch.Identifying = true;
            ch.Output = 100;
            Write(ch, 100);
            ch.Status = "Erkennen: dieser Lüfter läuft gerade auf 100 % …";
            return;
        }
        if (ch.Identifying)
        {
            ch.Identifying = false;
            // Hat der Lüfter auf 100 % reagiert?
            if (ch.IdentifyStartRpm >= 0 && ch.Rpm?.Value is float after)
            {
                float before = ch.IdentifyStartRpm;
                bool reacted = after - before >= Math.Max(80f, before * 0.10f) || after > 2500;
                if (!reacted && before < 2000)
                {
                    ch.NotControllable = true;
                    SafeDefault(ch);
                }
            }
            if (prof.Profile == FanProfile.Bios) SafeDefault(ch);
        }

        if (ch.NotControllable)
        {
            ch.Status = ch.Od8 != null
                ? "Lässt sich nicht steuern – der AMD-Treiber nimmt die Lüfterkurve nicht an. „Erkennen“ testet erneut."
                : ch.Od8Note != null
                ? $"Lässt sich nicht steuern – AMD Overdrive 8: {ch.Od8Note}. Details: %AppData%\\TempMonitor\\amd-od8.log"
                : ch.IsGpu
                ? "Lässt sich nicht steuern – der Grafiktreiber regelt diesen Lüfter selbst. „Erkennen“ testet erneut."
                : "Lässt sich nicht steuern – der Lüfter reagiert nicht auf Befehle. „Erkennen“ testet erneut.";
            return;
        }

        if (prof.Profile == FanProfile.Bios)
        {
            if (ch.Output >= 0) SafeDefault(ch);
            ch.Status = "Wird vom BIOS / Treiber geregelt";
            return;
        }
        if (ch.Faulted) return;

        // ----- Für jedes gewählte Bauteil die nötige Leistung bestimmen, die höchste gewinnt -----
        var targets = _s.TargetsFor(prof.Profile);
        float desired = float.MinValue;   // Smart-Werte dürfen auch unter 0 liegen (Bauteil sehr kühl)
        Component? driver = null;
        float? driverTemp = null;
        float driverTarget = 0;
        bool emergency = false;

        if (ch.Output < 0)
        {
            ch.Output = ch.ControlSensor.Value ?? 50f;
            if (ch.LastTestedPct < 0 && ch.Rpm?.Value is float r0)
            {
                ch.LastTestedPct = ch.Output;
                ch.TestStartRpm = r0;
            }
        }

        foreach (var c in Components.All)
        {
            if (!ch.Cfg.Uses(c) || Temp(c) is not float raw) continue;
            var st = ch.States[c];
            st.Smooth = st.Smooth is float sm ? sm + 0.35f * (raw - sm) : raw;
            float T = st.Smooth.Value;
            float target = targets[(int)c];

            float d = prof.Profile switch
            {
                FanProfile.Max => 100,
                FanProfile.Custom => Interpolate(ch.Cfg.Curve, T),
                _ => Adaptive(ch, c, st, T, target, prof, isDriver: ch.Driver == c),
            };

            if (_s.EmergencyEnabled && raw >= _s.Critical(c)) emergency = true;

            if (d > desired)
            {
                desired = d;
                driver = c;
                driverTemp = T;
                driverTarget = target;
            }
        }

        if (driver is null)
        {
            SafeDefault(ch);
            ch.Driver = null;
            ch.Status = "Keine Temperaturquelle gewählt – BIOS übernimmt";
            return;
        }
        ch.Driver = driver;
        ch.DriverTemp = driverTemp;

        float min = prof.MinPercent;
        if (ch.Kind == FanKind.Pump) min = Math.Max(min, _s.PumpMinPercent);
        min = Math.Max(min, ch.Control.MinSoftwareValue);
        float max = Math.Min(100f, ch.Control.MaxSoftwareValue > 0 ? ch.Control.MaxSoftwareValue : 100f);
        desired = Math.Clamp(desired, min, max);

        // Sanft ändern: schnell hoch, langsam runter
        float next = desired > ch.Output
            ? Math.Min(desired, ch.Output + prof.UpRate)
            : Math.Max(desired, ch.Output - prof.DownRate);

        string who = Components.Name(driver.Value);
        ch.Status = prof.Profile switch
        {
            FanProfile.Max => "Volle Leistung",
            FanProfile.Custom => $"Eigene Kurve · bestimmt von {who} ({driverTemp:0} °C)",
            _ => $"Smart · bestimmt von {who}: {driverTemp:0} °C, Ziel {driverTarget:0} °C · " +
                 $"{ch.Cfg.LearnedFor(prof.Profile, driver.Value).Count(v => v >= 0)}/11 Laststufen gelernt",
        };

        // Sicherheit (optional): zu heiß -> sofort volle Kühlung
        if (emergency)
        {
            next = max;
            ch.Status = "⚠ Notkühlung – Grenzwert erreicht";
        }

        next = Math.Clamp(next, min, max);
        ch.Output = next;
        Write(ch, next);
        if (ch.Od8?.LastError is string err)
            ch.Status += $" · {err}";
        CheckResponse(ch, next);
        if (ch.NotControllable) return;

        // Sicherheit (optional): Lüfter dreht trotz Befehl nicht -> zurück ans BIOS
        if (_s.StallDetection && ch.Rpm?.Value is float rpm && rpm < 50 && next >= 40)
        {
            if (++ch.NoSpinTicks >= 15)
            {
                SafeDefault(ch);
                ch.Faulted = true;
                ch.Status = "Lüfter reagiert nicht – BIOS übernimmt";
            }
        }
        else ch.NoSpinTicks = 0;
    }

    // ---------------------------------------------------------------------
    // Erfolgskontrolle: Bei einer deutlichen Änderung (≥ 25 %-Punkte) wird nach
    // 12 Sekunden geprüft, ob sich die Drehzahl passend bewegt hat. Zweimal keine
    // Reaktion -> Lüfter gilt als nicht steuerbar (z. B. AMD RX 6000/7000).
    // ---------------------------------------------------------------------
    static void CheckResponse(FanChannel ch, float now)
    {
        // AMD Overdrive 8: Der Treiber bestätigt jede Änderung. Hat er angenommen, gibt es keine
        // Drehzahl-Prüfung (die Karte regelt im Grenzbereich selbst mit -> sonst Fehlalarme).
        // Die bewusste Prüfung bleibt über "Erkennen".
        if (ch.Od8 != null && ch.Od8.LastError == null) return;

        // a) Rückmeldung des Treibers: meldet er dauerhaft eine ganz andere Leistung als befohlen?
        //    (bei AMD Overdrive 8 nicht, dort ist die Rückmeldung der Bibliothek nicht aussagekräftig)
        if (ch.Od8 == null && ch.ControlSensor.Value is float rb && rb >= 0 && rb <= 100)
        {
            if (Math.Abs(rb - now) > 15) ch.ReadbackMismatchTicks++;
            else ch.ReadbackMismatchTicks = 0;
            if (ch.ReadbackMismatchTicks >= 20)
            {
                ch.NotControllable = true;
                SafeDefault(ch);
                return;
            }
        }

        if (ch.Rpm?.Value is not float rpm) return;

        if (ch.TestStart is DateTime start)
        {
            if ((DateTime.Now - start).TotalSeconds < 12) return;
            ch.TestToPct = now;   // tatsächliche Änderung bis jetzt
            float dir = Math.Sign(ch.TestToPct - ch.TestFromPct);
            float change = (rpm - ch.TestStartRpm) * dir;
            bool reacted = change >= Math.Max(60f, ch.TestStartRpm * 0.08f);
            // Lüfter im Stillstand-Modus (0 U/min) und Befehl nach unten: nichts zu prüfen
            if (dir < 0 && ch.TestStartRpm < 100) reacted = true;
            // Großer Sprung ohne Reaktion: ein Fehlschlag genügt, sonst zwei
            int needed = Math.Abs(ch.TestToPct - ch.TestFromPct) >= 30 ? 1 : 2;
            if (reacted) ch.TestFailures = 0;
            else if (++ch.TestFailures >= needed)
            {
                ch.NotControllable = true;
                SafeDefault(ch);
            }
            ch.TestStart = null;
            ch.LastTestedPct = now;
            ch.TestStartRpm = rpm;
            return;
        }

        // Ausgangspunkt: Leistung + Drehzahl, bevor wir deutlich etwas ändern
        if (ch.LastTestedPct < 0) { ch.LastTestedPct = now; ch.TestStartRpm = rpm; return; }
        if (Math.Abs(now - ch.LastTestedPct) >= 25)
        {
            ch.TestStart = DateTime.Now;
            ch.TestFromPct = ch.LastTestedPct;
            ch.TestToPct = now;
            // TestStartRpm bleibt die Drehzahl vom Ausgangspunkt
        }
        else if (Math.Abs(now - ch.LastTestedPct) < 3)
        {
            ch.TestStartRpm = rpm;   // stabil: Ausgangsdrehzahl aktuell halten
        }
    }

    // ---------------------------------------------------------------------
    // Smart-Automatik: lernende Regelung je Bauteil
    //  1) Merkt sich pro Auslastungsstufe, welche Leistung die Zieltemperatur hält
    //     -> reagiert auf Laständerungen sofort (vorausschauend), bevor es heiß wird
    //  2) PI-Regler gleicht den Rest aus
    //  3) Passt die eigene Reaktionsstärke an: schwingt es, wird sie ruhiger;
    //     bleibt es zu lange daneben, wird sie kräftiger
    // ---------------------------------------------------------------------
    float Adaptive(FanChannel ch, Component c, SourceState st, float T, float target, ProfileInfo p, bool isDriver)
    {
        var cfg = ch.Cfg;
        float e = T - target;

        float load = Load(c) ?? 0f;
        int bucket = Math.Clamp((int)(load / 10f), 0, 10);
        var table = cfg.LearnedFor(p.Profile, c);

        if (st.LastBucket != bucket && st.LastBucket >= 0)
            st.Integral *= 0.5f;   // neue Last -> Grundwert übernimmt, Rest neu einregeln
        st.LastBucket = bucket;

        float basePct = LearnedBase(table, bucket, p.MinPercent);

        if (Math.Abs(e) > 0.8f) st.Integral += cfg.Ki * e;
        st.Integral = Math.Clamp(st.Integral, -60f, 60f);

        float output = basePct + cfg.Kp * e + st.Integral;

        // Anti-Windup
        if ((output >= 100 && e > 0) || (output <= p.MinPercent && e < 0))
            st.Integral -= cfg.Ki * e;

        // Gelernt wird nur vom Bauteil, das gerade wirklich die Drehzahl bestimmt
        if (!isDriver) return output;

        // --- Lernen 1: stabiler Zustand -> Grundwert für diese Last merken ---
        if (Math.Abs(e) < 1.5f) st.SteadyTicks++; else st.SteadyTicks = 0;
        if (st.SteadyTicks >= 20 && ch.Output >= 0)
        {
            table[bucket] = table[bucket] < 0 ? ch.Output : table[bucket] * 0.9f + ch.Output * 0.1f;
            st.Integral *= 0.9f;
        }

        // --- Lernen 2: Reaktionsstärke anpassen ---
        int sign = e > 1 ? 1 : e < -1 ? -1 : 0;
        if (sign != 0) st.Signs.Enqueue(sign);
        while (st.Signs.Count > 60) st.Signs.Dequeue();
        int flips = 0;
        int? prev = null;
        foreach (var s in st.Signs) { if (prev.HasValue && prev != s) flips++; prev = s; }
        if (flips > 8)
        {
            cfg.Kp = Math.Max(0.8f, cfg.Kp * 0.85f);
            cfg.Ki = Math.Max(0.03f, cfg.Ki * 0.85f);
            st.Signs.Clear();
        }

        bool saturated = output >= 99 || output <= p.MinPercent + 0.5f;
        if (Math.Abs(e) > 4 && !saturated) st.FarTicks++; else st.FarTicks = 0;
        if (st.FarTicks >= 45)
        {
            cfg.Kp = Math.Min(8f, cfg.Kp * 1.1f);
            cfg.Ki = Math.Min(0.6f, cfg.Ki * 1.15f);
            st.FarTicks = 0;
        }

        return output;
    }

    static float LearnedBase(float[] table, int bucket, float min)
    {
        if (table[bucket] >= 0) return table[bucket];
        for (int d = 1; d <= 10; d++)
        {
            if (bucket - d >= 0 && table[bucket - d] >= 0) return table[bucket - d] + d * 3;
            if (bucket + d <= 10 && table[bucket + d] >= 0) return Math.Max(min, table[bucket + d] - d * 3);
        }
        return min + 20;
    }

    public static float Interpolate(List<float[]> curve, float t)
    {
        var pts = curve.Where(p => p.Length == 2).OrderBy(p => p[0]).ToList();
        if (pts.Count == 0) return 50;
        if (t <= pts[0][0]) return pts[0][1];
        if (t >= pts[^1][0]) return pts[^1][1];
        for (int i = 1; i < pts.Count; i++)
        {
            if (t <= pts[i][0])
            {
                float x0 = pts[i - 1][0], y0 = pts[i - 1][1], x1 = pts[i][0], y1 = pts[i][1];
                return x1 - x0 < 0.01f ? y1 : y0 + (y1 - y0) * (t - x0) / (x1 - x0);
            }
        }
        return pts[^1][1];
    }
}
