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
        new(FanProfile.Eco,         "Energiesparend",   "So leise wie möglich",                         true,   20,   4, 0.5f, new float[] { 78, 75, 62, 90 }),
        new(FanProfile.Balanced,    "Ausgewogen",       "Ruhig und kühl",                               true,   25,   6, 0.8f, new float[] { 68, 70, 56, 85 }),
        new(FanProfile.Performance, "Leistung",         "Kühl für Spiele & Rendern",                    true,   35,  10, 0.8f, new float[] { 58, 62, 50, 78 }),
        new(FanProfile.Max,         "Maximale Kühlung", "Alle Lüfter dauerhaft auf 100 %",              false, 100, 100, 100, new float[] { 0, 0, 0, 0 }),
        new(FanProfile.Custom,      "Eigene Kurve",     "Für jeden Lüfter eine eigene Kurve festlegen", false,   0,   8, 1.5f, new float[] { 0, 0, 0, 0 }),
    };

    public static ProfileInfo Get(FanProfile p) => All.First(i => i.Profile == p);
}

sealed class FanGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Gruppe";
    /// Reihenfolge zählt: der erste (steuerbare) Lüfter führt die Gruppe
    public List<string> Members { get; set; } = new();
}

/// Programm -> Profil (z. B. Blender.exe -> Leistung)
sealed class AppRule
{
    public string Exe { get; set; } = "";
    public FanProfile Profile { get; set; } = FanProfile.Performance;
}

/// Zeitraum für den automatischen Profilwechsel. Days: Bit 0 = Montag … Bit 6 = Sonntag
sealed class ScheduleRule
{
    public string From { get; set; } = "22:00";
    public string To { get; set; } = "07:00";
    public FanProfile Profile { get; set; } = FanProfile.Eco;
    public int Days { get; set; } = 127;

    static int Minutes(string s) =>
        TimeSpan.TryParse(s, out var t) ? (int)t.TotalMinutes % 1440 : 0;

    static bool OnDay(int days, DateTime d) => (days & (1 << (((int)d.DayOfWeek + 6) % 7))) != 0;

    public bool Active(DateTime now)
    {
        int f = Minutes(From), t = Minutes(To), m = now.Hour * 60 + now.Minute;
        if (f == t) return OnDay(Days, now);                    // ganzer Tag
        if (f < t) return m >= f && m < t && OnDay(Days, now);
        // über Mitternacht: gehört zum Starttag
        return (m >= f && OnDay(Days, now)) || (m < t && OnDay(Days, now.AddDays(-1)));
    }
}

// ===========================================================================
// Einstellungen (werden in %AppData%\TempMonitor\fans.json gespeichert)
// ===========================================================================
sealed class FanChannelSettings
{
    public string? CustomName { get; set; }
    public FanKind? Kind { get; set; }           // null = automatisch erkennen
    /// Automatisch als Pumpe erkannt (Drehzahlverhalten), gilt solange Kind == null
    public bool DetectedPump { get; set; }
    /// Temperaturquellen hat der Nutzer selbst gewählt (dann nicht automatisch ändern)
    public bool SourcesUserSet { get; set; }
    /// Hardware + Anschlussname, um die Einstellungen nach geänderter Sensor-Kennung wiederzufinden
    public string? StableKey { get; set; }
    /// Lüfter meldet keine Drehzahl (Tacho-Leitung fehlt, z. B. hinter Hub/Splitter) – dann nicht nach Drehzahl beurteilen
    public bool NoTachSignal { get; set; }
    public string? PumpReason { get; set; }
    public bool SourcesInitialized { get; set; }
    public bool UseCpu { get; set; }
    public bool UseGpu { get; set; }
    public bool UseBoard { get; set; }
    public List<float[]> Curve { get; set; } = DefaultCurve();
    public float Kp { get; set; } = 3.0f;
    public float Ki { get; set; } = 0.15f;
    /// Gelernte Lüfterleistung je Auslastungsstufe (0–10 %, 10–20 % …) pro Profil und Bauteil; -1 = unbekannt
    public Dictionary<string, float[]> Learned { get; set; } = new();
    /// Gemessene Wirkung je Bauteil: um wie viel °C es kühler wird, wenn dieser Lüfter 10 % mehr läuft
    public Dictionary<string, float> Effect { get; set; } = new();

    // ----- Kalibrierung -----
    /// Ab dieser Leistung läuft der Lüfter sicher an (wird als Untergrenze genutzt)
    public float? StartPercent { get; set; }
    public float? MinRpm { get; set; }
    public float? MaxRpm { get; set; }
    public DateTime? CalibratedAt { get; set; }
    /// Darf im Leerlauf ganz stehen bleiben (wenn "Lüfter-Stopp im Leerlauf" an ist)
    public bool AllowStop { get; set; } = true;

    public float? EffectFor(Component c) => Effect.TryGetValue(c.ToString(), out var v) ? v : null;

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

    // Sicherheitsfunktionen – Notkühlung standardmäßig an (Schutz der Hardware geht vor)
    public bool EmergencyEnabled { get; set; } = true;
    public bool SafetyDefaultMigrated { get; set; } = true;
    public float CpuCritical { get; set; } = 90f;
    public float GpuCritical { get; set; } = 85f;
    public float BoardCritical { get; set; } = 80f;
    public float HotspotCritical { get; set; } = 105f;

    /// Mainboard-Grenze: Mainboard regelt nicht mit, dreht die Mainboard-Lüfter aber hoch, wenn es zu warm wird
    public bool BoardGuardEnabled { get; set; } = true;
    public float BoardLimit { get; set; } = 65f;
    public bool BoardDefaultMigrated { get; set; }
    public bool StallDetection { get; set; }

    /// Version des Lernverfahrens – bei Änderungen wird altes Gelerntes verworfen
    public int LearnVersion { get; set; }
    public const int CurrentLearnVersion = 3;
    const float WeakEffectLimit = 0.3f;

    /// Mindestleistung für Pumpen (Wasserkühlung), damit sie nie zu langsam laufen
    public float PumpMinPercent { get; set; } = 60f;

    public Dictionary<string, FanChannelSettings> Fans { get; set; } = new();

    // ----- Lüftergruppen: der erste Lüfter regelt, die anderen folgen -----
    public List<FanGroup> Groups { get; set; } = new();
    /// Anschlüsse, die trotz fehlender Drehzahl angezeigt werden sollen
    public List<string> ShowPorts { get; set; } = new();
    /// Zusätzliche Sicherung: Anschlüsse (stabiler Schlüssel), an denen eine Pumpe hängt – eine Pumpe darf nie als Lüfter behandelt werden
    public List<string> PumpKeys { get; set; } = new();
    /// Pumpen-Wächter: wie oft eine Pumpe ausgefallen/zu langsam war
    public int PumpAlarmCount { get; set; }
    public DateTime? PumpAlarmLast { get; set; }

    // ----- Automatischer Profilwechsel -----
    public bool AutoGame { get; set; }
    public FanProfile GameProfile { get; set; } = FanProfile.Performance;
    public bool GameDetectStores { get; set; } = true;
    public List<string> GameExes { get; set; } = new();
    public List<string> GameIgnore { get; set; } = new();
    public bool AutoSchedule { get; set; }
    public List<ScheduleRule> Schedule { get; set; } = new();
    public bool AutoNotify { get; set; } = true;
    public bool AutoApps { get; set; }
    public List<AppRule> AppRules { get; set; } = new();
    public bool AutoIdle { get; set; }
    public int IdleMinutes { get; set; } = 10;
    public FanProfile IdleProfile { get; set; } = FanProfile.Eco;

    // ----- Lüfter-Stopp im Leerlauf -----
    // ----- Laptop (Tongfang): Leistungsmodus folgt dem Lüfterprofil -----
    public bool LaptopControl { get; set; } = true;

    // ----- XMG Oasis (Bluetooth) -----
    public bool OasisEnabled { get; set; } = true;
    public ulong? OasisAddress { get; set; }
    /// Pumpenspannung: 0 = 11 V, 1 = 12 V, 2 = 7 V, 3 = 8 V
    public int OasisPumpVoltage { get; set; }
    /// Werte, wenn Toast Temperatur nicht steuert (BIOS-Profil, Beenden)
    public float OasisReleaseFan { get; set; } = 60f;
    public float OasisReleasePump { get; set; } = 100f;

    public bool ZeroRpmEnabled { get; set; }
    public float ZeroCpu { get; set; } = 50f;
    public float ZeroGpu { get; set; } = 50f;

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");
    static readonly string FilePath = Path.Combine(Dir, "fans.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static FanSettings Load()
    {
        try
        {
            if (SafeFile.ReadJson<FanSettings>(FilePath) is string json)
            {
                var s = JsonSerializer.Deserialize<FanSettings>(json) ?? new();
                if (!json.Contains("\"SafetyDefaultMigrated\""))
                {
                    // Einmalig: Notkühlung für bestehende Installationen einschalten (war früher standardmäßig aus)
                    s.EmergencyEnabled = true;
                    s.SafetyDefaultMigrated = true;
                }
                if (s.LearnVersion == 2)
                {
                    // Version 3: früher konnte die Wirkung negativ gemessen werden (Bauteil heizt gerade auf)
                    // -> Lüfter wurde fälschlich gedämpft. Nur diese Messwerte verwerfen, Gelerntes bleibt.
                    foreach (var f in s.Fans.Values)
                        foreach (var k in f.Effect.Where(kv => kv.Value < WeakEffectLimit).Select(kv => kv.Key).ToList())
                            f.Effect.Remove(k);
                    s.LearnVersion = 3;
                }
                if (s.LearnVersion < CurrentLearnVersion)
                {
                    // Neues Lernverfahren: alte (teils zu hoch gelernte) Werte verwerfen
                    foreach (var f in s.Fans.Values)
                    {
                        f.Learned.Clear();
                        f.Effect.Clear();
                        f.Kp = 3.0f;
                        f.Ki = 0.15f;
                    }
                    s.LearnVersion = CurrentLearnVersion;
                }
                if (!s.BoardDefaultMigrated)
                {
                    // Mainboard regelt standardmäßig nicht mehr mit (nur noch als Grenze)
                    foreach (var f in s.Fans.Values) f.UseBoard = false;
                    s.BoardDefaultMigrated = true;
                }
                return s;
            }
        }
        catch { /* kaputte Datei -> Standardwerte */ }
        return new() { LearnVersion = CurrentLearnVersion, BoardDefaultMigrated = true };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            SafeFile.Write(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* nicht kritisch */ }
    }

    public FanChannelSettings For(string id)
    {
        if (!Fans.TryGetValue(id, out var s)) { s = new(); Fans[id] = s; }
        return s;
    }

    /// Wie For, aber über einen stabilen Schlüssel (Hardware + Anschlussname): Ändert sich nach einem Neustart
    /// die interne Kennung des Sensors (z. B. andere Reihenfolge beim Erkennen), bleiben die Einstellungen erhalten.
    public FanChannelSettings ForStable(string id, string key, ISet<string> taken)
    {
        if (Fans.TryGetValue(id, out var s))
        {
            s.StableKey ??= key;
            return s;
        }
        var old = Fans.FirstOrDefault(kv => kv.Value.StableKey == key && !taken.Contains(kv.Key));
        if (old.Value != null)
        {
            Fans.Remove(old.Key);
            Fans[id] = old.Value;
            foreach (var g in Groups)
                for (int i = 0; i < g.Members.Count; i++)
                    if (g.Members[i] == old.Key) g.Members[i] = id;
            if (ShowPorts.Remove(old.Key)) ShowPorts.Add(id);
            ErrorLog.Write($"Lüfter-Einstellungen übernommen: Kennung {old.Key} -> {id} ({key})");
            return old.Value;
        }
        s = new() { StableKey = key };
        Fans[id] = s;
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
    public int BelowTicks;
    public readonly Queue<int> Signs = new();

    // Wirkungsmessung: Momentaufnahme von Temperatur und Leistung
    public DateTime? SnapTime;
    public float SnapT, SnapOut;
    public int SnapBucket = -1;
}

/// Nur Drehzahlanzeige, keine Steuerung
sealed class MonitorOnlyFan : IExternalFan
{
    public void SetPercent(float pct) { }
    public void Release() { }
    public bool Ready => false;
    public float? LastPercent => null;
    public string? Problem => "Nur Anzeige";
    public string? Alarm => null;
    public string? Note => null;
}

sealed class FanChannel
{
    /// Mainboard/GPU-Steuerung über LibreHardwareMonitor (null bei externen Geräten wie der XMG Oasis)
    public IControl? Control { get; init; }
    public ISensor? ControlSensor { get; init; }
    /// Externes Gerät (z. B. XMG Oasis über Bluetooth)
    public IExternalFan? Ext { get; init; }
    /// Zuletzt bekannte Leistung (Rückmeldung) in %
    public float? Readback => Ext != null ? Ext.LastPercent : ControlSensor?.Value;
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

    /// Nur Drehzahl, keine Steuerung (z. B. Pumpe an festem Anschluss)
    public bool MonitorOnly { get; init; }
    /// Anschlussname deutet auf eine Pumpe (Board-Beschriftung)
    public bool PumpHeader { get; init; }
    public int PumpTicks;

    static readonly System.Text.RegularExpressions.Regex PumpWords = new(
        @"pump|pumpe|\baio\b|aio[\s_]|water|wasser|\bw[_ ]?p\b|/wp|w_pump|kraken|liquid|coolant",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool LooksLikePump(string name) => PumpWords.IsMatch(name);

    public FanKind AutoKind =>
        PumpHeader || Cfg.DetectedPump || LooksLikePump(DefaultName) || LooksLikePump(HardwareName)
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
    /// Erkennen: true = anhalten (fällt am besten auf), false = hochdrehen (wenn er gerade steht)
    public bool IdentifyStop;
    public float IdentifyMinRpm = float.MaxValue, IdentifyMaxRpm;
    /// Ergebnis des letzten Erkennens (für die Nachfrage in der Oberfläche)
    public string? IdentifyResult;
    public volatile bool IdentifyFinished;
    public float IdentifyStartCpu, IdentifyStartGpu;
    // Pumpen-Wächter
    public float PumpBaseline;
    public int PumpLowTicks, PumpOkTicks;
    public DateTime IdentifyEndedAt = DateTime.MinValue;
    public int ReadbackMismatchTicks;
    public float? DriverTemp;

    // Kalibrierung: 0 = aus, 1 = 100 % messen, 2 = 0 % (bleibt er stehen?), 3 = langsam hochfahren bis er anläuft
    public int CalPhase;
    public DateTime CalNext;
    public float CalPct;
    public string? CalNote;

    // Lüfter-Stopp im Leerlauf
    public bool ZeroStopped;
    public DateTime? QuietSince;
    public DateTime KickUntil = DateTime.MinValue;
    public DateTime LastUpAt = DateTime.MinValue;
    public DateTime RestartedAt = DateTime.MinValue;
    public readonly Dictionary<Component, SourceState> States = new()
    {
        [Component.Cpu] = new(), [Component.Gpu] = new(), [Component.Board] = new(), [Component.Hotspot] = new(),
    };

    public void ResetStates()
    {
        foreach (var c in Components.All) States[c] = new SourceState();
        Driver = null;
        ZeroStopped = false;
        QuietSince = null;
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
    /// Das gerade wirksame Profil (Automatik geht vor der eigenen Wahl)
    public FanProfile Profile => Override ?? _s.Profile;
    /// Das von Hand gewählte Profil
    public FanProfile ManualProfile => _s.Profile;
    /// Vom automatischen Profilwechsel gesetzt (Spiel / Zeitplan), sonst null
    public FanProfile? Override { get; private set; }
    public string? OverrideReason { get; private set; }
    /// Hat man während einer Automatik selbst gewählt, bleibt diese Automatik aus, bis ihr Anlass endet
    public string? HeldReason { get; set; }

    /// Wird vom automatischen Profilwechsel aufgerufen. true = Profil hat sich geändert.
    public bool SetOverride(FanProfile? p, string? reason)
    {
        lock (_lock)
        {
            if (p == _s.Profile) p = null;
            if (p == null) reason = null;
            if (p == Override) { OverrideReason = reason; return false; }
            Override = p;
            OverrideReason = reason;
            ResetForProfile(Profile);
            return true;
        }
    }

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
        HiddenPorts.Clear();

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
            bool connected = gpu || (rpm?.Value ?? 0) > 0 || _s.ShowPorts.Contains(sensor.Control!.Identifier.ToString());
            if (!connected)
            {
                var (t0, s0, r0, b0, f0) = (top, sensor, rpm, baseName, fans);
                HiddenPorts.Add(new HiddenPort(sensor.Control!.Identifier.ToString(), FriendlyName(rpm?.Name ?? baseName), top.Name,
                    sensor.Control!, rpm, () => AddBoardChannel(t0, s0, r0, b0, false, f0)));
                HiddenCount++;
                FanNaming.Hidden.Add(sensor.Identifier.ToString());
                if (rpm != null) FanNaming.Hidden.Add(rpm.Identifier.ToString());
                continue;
            }

            AddBoardChannel(top, sensor, rpm, baseName, gpu, fans);
        }

        // Drehzahl ohne Steuerung (z. B. Pumpe an einem festen Anschluss, AIO-Pumpe über USB):
        // trotzdem anzeigen, damit man sie sieht und ein Ausfall auffällt
        if (!gpu)
        {
            foreach (var f in fans)
            {
                string fid = f.Identifier.ToString();
                if (FanNaming.BySensor.ContainsKey(fid) || (f.Value ?? 0) <= 0) continue;
                var cfg = _s.For(fid);
                var ch = new FanChannel
                {
                    MonitorOnly = true,
                    PumpHeader = IsPumpHeader(top, f.Name),
                    Ext = new MonitorOnlyFan(),
                    Rpm = f,
                    Id = fid,
                    DefaultName = top.HardwareType == HardwareType.Cooler ? $"{top.Name} · {f.Name}" : FriendlyName(f.Name),
                    HardwareName = top.Name,
                    Cfg = cfg,
                    NotControllable = true,
                };
                if (!cfg.SourcesInitialized) { cfg.UseCpu = true; cfg.SourcesInitialized = true; }
                Channels.Add(ch);
                FanNaming.BySensor[fid] = ch;
            }
        }
        foreach (var sub in hw.SubHardware) AddChannels(top, sub);
    }

    void AddBoardChannel(IHardware top, ISensor sensor, ISensor? rpm, string baseName, bool gpu, List<ISensor> fans)
    {
        string id = sensor.Control.Identifier.ToString();
        var cfg = _s.ForStable(id, $"{top.Name}|{(gpu ? "gpu" : "")}{rpm?.Name ?? baseName}", Channels.Select(c => c.Id).ToHashSet());

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
            PumpHeader = !gpu && IsPumpHeader(top, rpm?.Name ?? baseName),
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
            else cfg.UseCpu = cfg.UseGpu = true;   // Gehäuselüfter: CPU + GPU (Mainboard nur als Grenze)
            cfg.SourcesInitialized = true;
        }
        // Einmal als Pumpe bekannt -> bleibt Pumpe (außer der Nutzer stellt ausdrücklich „Lüfter“ ein)
        if (!gpu && cfg.Kind == null && cfg.StableKey != null && _s.PumpKeys.Contains(cfg.StableKey)) cfg.DetectedPump = true;
        if (!gpu && ch.Kind == FanKind.Pump) PumpSources(cfg);

        Channels.Add(ch);
        FanNaming.BySensor[sensor.Identifier.ToString()] = ch;
        if (rpm != null) FanNaming.BySensor[rpm.Identifier.ToString()] = ch;
    }

    /// Leerer bzw. ausgeblendeter Anschluss (keine Drehzahl beim Start)
    public sealed record HiddenPort(string Id, string Name, string HardwareName, IControl Control, ISensor? Rpm, Action Add);
    public List<HiddenPort> HiddenPorts { get; } = new();

    /// Ausgeblendeten Anschluss dauerhaft anzeigen und steuern
    /// Pumpe (Wasserkühlung am Prozessor): regelt nur nach der CPU, außer der Nutzer hat es anders gewählt
    static void PumpSources(FanChannelSettings cfg)
    {
        if (cfg.SourcesUserSet) return;
        cfg.UseCpu = true;
        cfg.UseGpu = false;
        cfg.UseBoard = false;
    }

    public void ShowPort(HiddenPort p)
    {
        lock (_lock)
        {
            if (!HiddenPorts.Remove(p)) return;
            if (!_s.ShowPorts.Contains(p.Id)) _s.ShowPorts.Add(p.Id);
            HiddenCount = Math.Max(0, HiddenCount - 1);
            p.Add();
            _s.Save();
        }
    }

    /// Ausgeblendete Anschlüsse prüfen: kurz auf 100 % – wo sich danach etwas dreht, hängt ein Lüfter
    /// (z. B. Lüfter ohne Drehzahlsignal im Stillstand, oder im Leerlauf stehende Lüfter)
    public async Task<List<HiddenPort>> ProbeHiddenAsync()
    {
        List<HiddenPort> ports;
        lock (_lock) ports = HiddenPorts.ToList();
        var found = new List<HiddenPort>();
        foreach (var p in ports) { try { p.Control.SetSoftware(100); } catch { } }
        await Task.Delay(7000);
        foreach (var p in ports)
        {
            try { p.Rpm?.Hardware.Update(); } catch { }
            if ((p.Rpm?.Value ?? 0) > 100) found.Add(p);
            try { p.Control.SetDefault(); } catch { }
        }
        foreach (var p in found) ShowPort(p);
        return found;
    }

    /// Board-Beschriftung des Anschlusses: Pumpe? (W_PUMP, AIO_PUMP, PUMP_FAN, ASRock CPU_FAN2/WP …)
    static bool IsPumpHeader(IHardware top, string name)
    {
        if (FanChannel.LooksLikePump(name)) return true;
        if (top.HardwareType == HardwareType.Cooler && name.Contains("Pump", StringComparison.OrdinalIgnoreCase)) return true;
        // ASRock: der zweite CPU-Lüfteranschluss heißt CPU_FAN2/WP und ist für Pumpen gedacht
        if (top.Name.Contains("ASRock", StringComparison.OrdinalIgnoreCase)
            && System.Text.RegularExpressions.Regex.IsMatch(name, @"cpu[\s_]*fan[\s_#]*2", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    /// Pumpe am Drehzahlverhalten erkennen: dreht sehr hoch bei wenig Leistung,
    /// oder lässt sich nicht regeln und dreht trotzdem hoch (3-Pin-Pumpe mit fester Drehzahl)
    void DetectPump(FanChannel ch)
    {
        if (ch.IsGpu || ch.Cfg.Kind != null || ch.Cfg.DetectedPump || ch.Ext is OasisFan or UniwillFan) return;
        if (ch.Rpm?.Value is not float rpm || rpm <= 0) return;
        string? reason = null;
        float? duty = ch.MonitorOnly ? null : ch.ControlSensor?.Value;
        // Pumpen drehen auch bei wenig Leistung sehr hoch (Lüfter bei 40 % meist unter 1.200 U/min)
        if (duty is float d && d > 0 && (d <= 55 && rpm >= 2300 || d <= 40 && rpm >= 1800))
        {
            if (++ch.PumpTicks >= 30) reason = $"dreht {rpm:N0} U/min bei nur {d:0} % – typisch für eine Pumpe";
        }
        else if ((ch.NotControllable || ch.MonitorOnly) && rpm >= 1600)
        {
            if (++ch.PumpTicks >= 30) reason = $"feste Drehzahl {rpm:N0} U/min – typisch für eine Pumpe";
        }
        else ch.PumpTicks = 0;
        if (reason == null) return;
        ch.Cfg.DetectedPump = true;
        ch.Cfg.PumpReason = reason;
        PumpSources(ch.Cfg);
        _s.Save();
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
            if (Override != null) HeldReason = OverrideReason;
            Override = null;
            OverrideReason = null;
            _s.Profile = p;
            ResetForProfile(p);
            _s.Save();
        }
    }

    void ResetForProfile(FanProfile p)
    {
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
    }

    // ---------------------------------------------------------------------
    // Kalibrierung: höchste Drehzahl messen und herausfinden, ab wie viel Prozent
    // der Lüfter sicher anläuft. Alle Lüfter gleichzeitig, dauert 1–2 Minuten.
    // ---------------------------------------------------------------------
    public bool Calibrating => Channels.Any(c => c.CalPhase > 0);

    /// Laptop-Lüfter über die Tongfang-Firmware (nur Modus, keine eigene Kurve)
    public TongfangLaptop? Laptop { get; set; }
    public string LaptopStatus { get; private set; } = "";
    int? _laptopWant;
    DateTime _laptopRead = DateTime.MinValue, _laptopSet = DateTime.MinValue;
    int _laptopStep;          // 0 = Grundmodus, 1 = eine Stufe kräftiger (zu warm)
    DateTime _laptopStepAt = DateTime.MinValue;

    void TickLaptop(ProfileInfo prof)
    {
        var l = Laptop;
        if (l == null) return;
        var now = DateTime.Now;
        if ((now - _laptopRead).TotalSeconds >= 3) { _laptopRead = now; l.Refresh(); }

        if (!_s.LaptopControl || prof.Profile == FanProfile.Bios)
        {
            if (l.Changed) l.Restore();
            _laptopWant = null;
            LaptopStatus = !_s.LaptopControl ? "Steuert das Control Center" : "Profil „Standard (BIOS)“ – Laptop regelt selbst";
            return;
        }

        // Grundmodus je Profil
        int baseMode = prof.Profile switch
        {
            FanProfile.Eco => TongfangLaptop.ModeQuiet,
            FanProfile.Performance or FanProfile.Max => TongfangLaptop.ModePerformance,
            _ => TongfangLaptop.ModeBalanced,
        };

        // Smart: wird CPU oder GPU deutlich wärmer als das Ziel, eine Stufe kräftiger – mit Hysterese
        string why = "";
        if (prof.Adaptive)
        {
            var targets = _s.TargetsFor(prof.Profile);
            float over = new[] { Component.Cpu, Component.Gpu }
                .Select(c => Temp(c) is float t ? t - targets[(int)c] : float.MinValue).Max();
            if (_laptopStep == 0 && over >= 8 && (now - _laptopStepAt).TotalSeconds >= 20) { _laptopStep = 1; _laptopStepAt = now; }
            else if (_laptopStep == 1 && over <= -2 && (now - _laptopStepAt).TotalSeconds >= 60) { _laptopStep = 0; _laptopStepAt = now; }
            if (_laptopStep == 1) why = " · wärmer als das Ziel → eine Stufe kräftiger";
        }
        else _laptopStep = 0;

        int mode = baseMode;
        if (_laptopStep == 1) mode = baseMode == TongfangLaptop.ModeQuiet ? TongfangLaptop.ModeBalanced : TongfangLaptop.ModePerformance;

        bool hot = _s.EmergencyEnabled && Components.All.Any(c => Temp(c) is float t && t >= _s.Critical(c));
        bool boost = prof.Profile == FanProfile.Max || hot;

        if (mode != _laptopWant)
        {
            if (mode == TongfangLaptop.ModePerformance && l.OnUsbC()) { mode = TongfangLaptop.ModeBalanced; why += " · am USB-C-Netzteil kein Leistungsmodus"; }
            _laptopWant = mode;
            _laptopSet = now;
            l.SetMode(mode);
        }
        else if (l.Mode != mode && (now - _laptopSet).TotalSeconds >= 15)
        {
            // Jemand (z. B. das Control Center) hat umgestellt -> zurückholen
            _laptopSet = now;
            l.SetMode(mode);
        }
        if (boost != l.Boost) l.SetBoost(boost);

        LaptopStatus = $"Modus {TongfangLaptop.ModeName(l.Mode)}" + (l.Boost ? " · Lüfter-Boost an" : "") + why +
                       (l.LastError != null ? $" · Fehler: {l.LastError}" : "");
    }

    /// Wird ausgelöst, wenn später Lüfter dazukommen (z. B. XMG Oasis gefunden)
    public event Action? ChannelsChanged;

    /// Laptop-Lüfter (Uniwill-EC) als zwei steuerbare Kanäle
    public UniwillEc? Uniwill { get; private set; }

    public void AddUniwill(UniwillEc ec)
    {
        lock (_lock)
        {
            Uniwill = ec;
            foreach (int fan in new[] { 0, 1 })
            {
                string id = fan == 0 ? "uniwill/fan1" : "uniwill/fan2";
                var cfg = _s.For(id);
                if (!cfg.SourcesInitialized)
                {
                    // Laptop: CPU und GPU teilen sich meist die Heatpipes -> beide Lüfter achten auf beide
                    cfg.UseCpu = cfg.UseGpu = true;
                    cfg.SourcesInitialized = true;
                }
                Channels.Add(new FanChannel
                {
                    Ext = new UniwillFan(ec, fan),
                    Id = id,
                    DefaultName = fan == 0 ? "Laptop · CPU-Lüfter" : "Laptop · GPU-Lüfter",
                    HardwareName = "Laptop (Embedded Controller)" + (ec.Board.Length > 0 ? $" · {ec.Board}" : ""),
                    Cfg = cfg,
                });
            }
        }
    }

    public void AddOasis(OasisLink link)
    {
        lock (_lock)
        {
            if (Channels.Any(c => c.Ext is OasisFan)) return;
            foreach (bool pump in new[] { false, true })
            {
                string id = pump ? "oasis/pump" : "oasis/fan";
                var cfg = _s.For(id);
                if (!cfg.SourcesInitialized)
                {
                    cfg.UseCpu = cfg.UseGpu = true;   // externe Wasserkühlung kühlt CPU und GPU des Laptops
                    cfg.SourcesInitialized = true;
                }
                Channels.Add(new FanChannel
                {
                    Ext = new OasisFan(link, _s, pump),
                    Id = id,
                    DefaultName = pump ? "XMG Oasis · Pumpe" : "XMG Oasis · Lüfter",
                    HardwareName = "XMG Oasis (Bluetooth)",
                    Cfg = cfg,
                });
            }
        }
        ChannelsChanged?.Invoke();
    }

    public IEnumerable<FanChannel> Calibratable => Channels.Where(c => !c.IsGpu && c.Rpm != null && !c.NotControllable);

    public void StartCalibration()
    {
        lock (_lock)
        {
            foreach (var ch in Calibratable)
            {
                ch.CalPhase = 1;
                ch.CalPct = 100;
                ch.CalNext = DateTime.Now.AddSeconds(10);
                ch.CalNote = null;
                Write(ch, 100);
                ch.Output = 100;
            }
        }
    }

    public void CancelCalibration(string? why = null)
    {
        lock (_lock)
        {
            foreach (var ch in Channels.Where(c => c.CalPhase > 0))
            {
                ch.CalPhase = 0;
                ch.CalNote = why ?? "abgebrochen";
                ch.ResetStates();
                SafeDefault(ch);
            }
        }
    }

    void CalibrateStep(FanChannel ch)
    {
        // Sicherheit: während der Messung laufen Lüfter langsam – bei Hitze sofort abbrechen
        if ((Temp(Component.Cpu) ?? 0) > 85 || (Temp(Component.Gpu) ?? 0) > 85 || (Temp(Component.Board) ?? 0) > 75)
        {
            foreach (var c in Channels.Where(c => c.CalPhase > 0))
            {
                c.CalPhase = 0;
                c.CalNote = "abgebrochen – zu warm, bitte im Leerlauf kalibrieren";
                c.ResetStates();
                SafeDefault(c);
            }
            return;
        }

        float rpm = ch.Rpm?.Value ?? 0;
        if (DateTime.Now < ch.CalNext)
        {
            Write(ch, ch.CalPct);
            ch.Output = ch.CalPct;
            ch.Status = ch.CalPhase switch
            {
                1 => "Kalibrierung: 100 % – höchste Drehzahl messen …",
                2 => "Kalibrierung: 0 % – bleibt der Lüfter stehen?",
                _ => $"Kalibrierung: {ch.CalPct:0} % – läuft er schon an?",
            };
            return;
        }

        void Done(string note)
        {
            ch.CalPhase = 0;
            ch.CalNote = note;
            ch.Cfg.CalibratedAt = DateTime.Now;
            ch.ResetStates();
            SafeDefault(ch);   // nächster Takt übernimmt wieder das Profil (bzw. das BIOS)
            ch.Status = "Kalibrierung fertig: " + note;
            _s.Save();
        }

        switch (ch.CalPhase)
        {
            case 1:
                ch.Cfg.MaxRpm = rpm;
                if (ch.Kind == FanKind.Pump)
                {
                    // Pumpen nie anhalten
                    ch.Cfg.StartPercent = null;
                    Done($"Pumpe, höchstens {rpm:N0} U/min");
                    return;
                }
                if (rpm < 150)
                {
                    ch.Cfg.StartPercent = null;
                    Done("dreht auch bei 100 % nicht – Anschluss leer oder nicht steuerbar");
                    return;
                }
                ch.CalPhase = 2;
                ch.CalPct = 0;
                ch.CalNext = DateTime.Now.AddSeconds(10);
                break;

            case 2:
                if (rpm >= 150)
                {
                    // Bleibt nie stehen (Mindestdrehzahl vom Mainboard/Lüfter) -> keine Untergrenze nötig
                    ch.Cfg.MinRpm = rpm;
                    ch.Cfg.StartPercent = null;
                    Done($"läuft immer (mind. {rpm:N0} U/min), höchstens {ch.Cfg.MaxRpm:N0} U/min");
                    return;
                }
                ch.CalPhase = 3;
                ch.CalPct = 5;
                ch.CalNext = DateTime.Now.AddSeconds(4);
                break;

            default:
                if (rpm >= 150)
                {
                    ch.Cfg.MinRpm = rpm;
                    ch.Cfg.StartPercent = Math.Min(100, ch.CalPct + 5);   // etwas Reserve
                    Done($"läuft ab {ch.CalPct:0} % an (Untergrenze {ch.Cfg.StartPercent:0} %), höchstens {ch.Cfg.MaxRpm:N0} U/min");
                    return;
                }
                ch.CalPct += 5;
                if (ch.CalPct > 70)
                {
                    ch.Cfg.StartPercent = null;
                    Done("läuft erst über 70 % an – keine Untergrenze gesetzt");
                    return;
                }
                ch.CalNext = DateTime.Now.AddSeconds(4);
                break;
        }
        Write(ch, ch.CalPct);
        ch.Output = ch.CalPct;
    }

    // ---------------------------------------------------------------------
    // Lüftergruppen
    // ---------------------------------------------------------------------
    /// Lüfter, die in Gruppen dürfen (Grafikkarten-Lüfter regelt der Grafiktreiber eigenständig)
    public IEnumerable<FanChannel> Groupable => Channels.Where(c => !c.IsGpu);

    public FanChannel? ById(string id) => Channels.FirstOrDefault(c => c.Id == id);

    public FanGroup? GroupOf(FanChannel ch) => _s.Groups.FirstOrDefault(g => g.Members.Contains(ch.Id));

    /// Der Lüfter, der für die Gruppe regelt: der erste angeschlossene, steuerbare
    public FanChannel? LeaderOf(FanGroup g) =>
        g.Members.Select(ById).FirstOrDefault(c => c != null && !c.NotControllable);

    public IEnumerable<FanChannel> MembersOf(FanGroup g) =>
        g.Members.Select(ById).Where(c => c != null).Cast<FanChannel>();

    public bool IsFollower(FanChannel ch) =>
        GroupOf(ch) is FanGroup g && LeaderOf(g) is FanChannel l && l != ch;

    public void SetGroup(FanChannel ch, FanGroup? group)
    {
        lock (_lock)
        {
            var left = _s.Groups.Where(g => g.Members.Remove(ch.Id)).ToList();
            if (group != null && !ch.IsGpu) group.Members.Add(ch.Id);
            // Gruppe, die dadurch leer wird, verschwindet
            _s.Groups.RemoveAll(g => left.Contains(g) && g.Members.Count == 0 && g != group);
            ch.ResetStates();
            _s.Save();
        }
    }

    public FanGroup NewGroup(string name)
    {
        lock (_lock)
        {
            var g = new FanGroup { Name = string.IsNullOrWhiteSpace(name) ? $"Gruppe {_s.Groups.Count + 1}" : name.Trim() };
            _s.Groups.Add(g);
            return g;
        }
    }

    public void RemoveGroup(FanGroup g)
    {
        lock (_lock)
        {
            _s.Groups.Remove(g);
            foreach (var ch in Channels) ch.ResetStates();
            _s.Save();
        }
    }

    public void SetLeader(FanGroup g, FanChannel ch)
    {
        lock (_lock)
        {
            if (!g.Members.Remove(ch.Id)) return;
            g.Members.Insert(0, ch.Id);
            foreach (var m in MembersOf(g)) m.ResetStates();
            _s.Save();
        }
    }

    public void ReplaceGroups(List<FanGroup> groups)
    {
        lock (_lock)
        {
            _s.Groups = groups;
            foreach (var ch in Channels) ch.ResetStates();
            _s.Save();
        }
    }

    /// Automatische Erkennung: gleiche Namen (Front 1, Front 2 …), Pumpen, und Lüfter,
    /// die nach denselben Bauteilen regeln (z. B. beide Lüfter eines CPU-Kühlers)
    public List<FanGroup> SuggestGroups()
    {
        var result = new List<FanGroup>();
        var free = Groupable.Where(c => !c.NotControllable).ToList();

        // 1) Eigene Namen mit gleichem Anfang: "Front oben", "Front unten", "Front 3" -> "Front"
        string Stem(string n)
        {
            n = System.Text.RegularExpressions.Regex.Replace(n.Trim(), @"[\s#\-_]*\d+$", "");
            var first = n.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? n;
            return first.Length >= 3 ? first : n;
        }
        foreach (var grp in free.Where(c => !string.IsNullOrWhiteSpace(c.Cfg.CustomName))
                                .GroupBy(c => Stem(c.Cfg.CustomName!), StringComparer.OrdinalIgnoreCase)
                                .Where(g => g.Count() >= 2).ToList())
        {
            var name = char.ToUpper(grp.Key[0]) + grp.Key[1..];
            result.Add(new FanGroup { Name = name, Members = grp.Select(c => c.Id).ToList() });
            foreach (var c in grp) free.Remove(c);
        }

        // 2) Pumpen
        var pumps = free.Where(c => c.Kind == FanKind.Pump).ToList();
        if (pumps.Count >= 2)
        {
            result.Add(new FanGroup { Name = "Pumpen", Members = pumps.Select(c => c.Id).ToList() });
            foreach (var c in pumps) free.Remove(c);
        }
        free.RemoveAll(c => c.Kind == FanKind.Pump);

        // 3) Gleiche Temperaturquellen
        foreach (var grp in free.GroupBy(c => (c.Cfg.UseCpu, c.Cfg.UseGpu, c.Cfg.UseBoard)).Where(g => g.Count() >= 2))
        {
            var (cpu, gpu, board) = grp.Key;
            string name = cpu && !gpu ? "CPU-Kühler" : gpu ? "Gehäuselüfter" : board ? "Mainboard-Lüfter" : "Lüfter";
            if (result.Any(r => r.Name == name)) name += $" {result.Count(r => r.Name.StartsWith(name)) + 1}";
            result.Add(new FanGroup { Name = name, Members = grp.Select(c => c.Id).ToList() });
        }
        return result;
    }

    /// Lässt einen Lüfter 5 Sekunden auf 100 % laufen, damit man ihn zuordnen kann
    public void Identify(FanChannel ch)
    {
        lock (_lock)
        {
            float rpm = ch.Rpm?.Value ?? -1;
            // Drehender Lüfter: anhalten – Stillstand sieht und hört man sofort. Steht er gerade: hochdrehen.
            ch.IdentifyStop = (rpm < 0 || rpm >= 300) && SafeToStop(out _);
            ch.IdentifyStartCpu = Temp(Component.Cpu) ?? 0;
            ch.IdentifyStartGpu = Temp(Component.Hotspot) ?? Temp(Component.Gpu) ?? 0;
            // Pumpen nur kurz anhalten (Wasser puffert die Wärme), Lüfter etwas länger
            ch.IdentifyUntil = DateTime.Now.AddSeconds(ch.IdentifyStop && ch.Kind == FanKind.Pump ? 5 : 7);
            ch.IdentifyStartRpm = rpm;
            ch.IdentifyMinRpm = float.MaxValue;
            ch.IdentifyMaxRpm = 0;
            ch.IdentifyResult = null;
            ch.IdentifyFinished = false;
            ch.NotControllable = false;
            ch.TestFailures = 0;
            ch.TestStart = null;
            ch.Faulted = false;
        }
    }

    /// Darf ein Lüfter zum Erkennen kurz angehalten werden? Nur wenn alles kühl ist (Schutz geht vor).
    public bool SafeToStop(out string? why)
    {
        why = null;
        if (Temp(Component.Cpu) is float c && c >= 75) why = $"CPU ist {c:0} °C warm";
        else if (Temp(Component.Gpu) is float g && g >= 80) why = $"Grafikkarte ist {g:0} °C warm";
        else if (Temp(Component.Hotspot) is float h && h >= 90) why = $"GPU-Hot-Spot ist {h:0} °C warm";
        else if (Temp(Component.Board) is float b && b >= 60) why = $"Mainboard ist {b:0} °C warm";
        return why == null;
    }

    public void ResetLearning(FanChannel ch)
    {
        lock (_lock)
        {
            ch.Cfg.Learned.Clear();
            ch.Cfg.Effect.Clear();
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
            try { Laptop?.Restore(); } catch { }
            try { Uniwill?.Release(); } catch { }
            _s.Save();
        }
    }

    static void SafeDefault(FanChannel ch)
    {
        if (ch.Od8 != null) { try { ch.Od8.Restore(); } catch { } }
        if (ch.Ext != null) { try { ch.Ext.Release(); } catch { } }
        try { ch.Control?.SetDefault(); } catch { }
        ch.Output = -1;
    }

    /// Drehzahl setzen – bei AMD RX 6000+ über Overdrive 8, sonst über die Bibliothek
    static void Write(FanChannel ch, float pct)
    {
        if (ch.Od8 != null) ch.Od8.SetPercent(pct);
        else if (ch.Ext != null) ch.Ext.SetPercent(pct);
        else ch.Control?.SetSoftware(pct);
    }

    // ---------------------------------------------------------------------
    // Einmal pro Sekunde (nach dem Sensor-Update)
    // ---------------------------------------------------------------------
    public void Tick()
    {
        lock (_lock)
        {
            var prof = Profiles.Get(Profile);
            try { TickLaptop(prof); } catch (Exception ex) { LaptopStatus = "Fehler: " + ex.Message; }
            try { Uniwill?.Tick(); } catch { }
            try { PumpGuard(); } catch { }
            // Erst die regelnden Lüfter, dann die Gruppen-Mitglieder, die ihnen folgen
            foreach (bool followers in new[] { false, true })
            {
                foreach (var ch in Channels)
                {
                    if (IsFollower(ch) != followers) continue;
                    try
                    {
                        if (followers) TickFollower(ch, prof);
                        else TickChannel(ch, prof);
                    }
                    catch (Exception ex)
                    {
                        SafeDefault(ch);
                        ch.Faulted = true;
                        ch.Status = "Fehler – BIOS übernimmt: " + ex.Message;
                    }
                }
            }

            if ((DateTime.Now - _lastSave).TotalSeconds > 60)
            {
                _s.Save();
                _lastSave = DateTime.Now;
            }
        }
    }

    /// Pumpe ausgefallen/zu langsam -> Text der Warnung, sonst null. Solange gesetzt: alle Lüfter voll.
    public string? PumpAlarm { get; private set; }
    /// Wird ausgelöst, wenn eine Pumpe ausfällt (für die Benachrichtigung)
    public event Action<string>? PumpAlarmRaised;

    /// Pumpen-Wächter: Jede Pumpe mit Drehzahlsignal wird jede Sekunde geprüft. Fällt sie unter 300 U/min oder
    /// unter 35 % ihrer üblichen Drehzahl (4 s lang), werden sofort alle Lüfter auf 100 % gestellt und gewarnt.
    void PumpGuard()
    {
        string? alarm = null;
        foreach (var ch in Channels.Where(c => c.Kind == FanKind.Pump && c.Rpm != null && c.Ext is not OasisFan))
        {
            if (ch.Identifying || ch.CalPhase > 0 || (DateTime.Now - ch.IdentifyEndedAt).TotalSeconds < 15) { ch.PumpLowTicks = 0; continue; }
            if (ch.Rpm!.Value is not float rpm) continue;
            bool low = rpm < 300 || (ch.PumpBaseline > 800 && rpm < ch.PumpBaseline * 0.35f);
            if (!low)
            {
                ch.PumpBaseline = ch.PumpBaseline <= 0 ? rpm : ch.PumpBaseline * 0.98f + rpm * 0.02f;
                ch.PumpLowTicks = 0;
                continue;
            }
            // Nur wenn die Pumpe in dieser Sitzung schon einmal normal lief (sonst: Pumpe ohne Drehzahlsignal)
            if (ch.PumpBaseline <= 0) continue;
            if (++ch.PumpLowTicks >= 4)
                alarm ??= $"{ch.DisplayName}: nur {rpm:N0} U/min (sonst ca. {ch.PumpBaseline:N0})";
        }

        if (alarm != null && PumpAlarm == null)
        {
            PumpAlarm = alarm;
            _s.PumpAlarmCount++;
            _s.PumpAlarmLast = DateTime.Now;
            _s.Save();
            ErrorLog.Write("Pumpen-Wächter: " + alarm);
            try { PumpAlarmRaised?.Invoke(alarm); } catch { }
        }
        else if (alarm == null && PumpAlarm != null)
        {
            // Erst nach 15 s stabilem Lauf aufheben
            if (Channels.Where(c => c.Kind == FanKind.Pump && c.Rpm != null).All(c => c.PumpLowTicks == 0))
            {
                foreach (var c in Channels) c.PumpOkTicks++;
                if (Channels.FirstOrDefault()?.PumpOkTicks >= 15)
                {
                    PumpAlarm = null;
                    foreach (var c in Channels) c.PumpOkTicks = 0;
                }
            }
        }
        else if (alarm != null) foreach (var c in Channels) c.PumpOkTicks = 0;
    }

    /// Gemeinsamer Anfang: Kalibrierung, Erkennen, nicht steuerbar, BIOS. true = Lüfter ist damit fertig.
    bool Prelude(FanChannel ch, ProfileInfo prof)
    {
        // Pumpe ausgefallen: alles auf volle Kühlung, Vorrang vor allem anderen
        if (PumpAlarm != null && !ch.MonitorOnly && ch.Ext is not { Ready: false })
        {
            ch.Output = 100;
            Write(ch, 100);
            ch.Driver = null;
            ch.Status = "⚠ Pumpen-Wächter: " + PumpAlarm + " – volle Kühlung";
            return true;
        }
        try { DetectPump(ch); } catch { }

        if (ch.CalPhase > 0)
        {
            CalibrateStep(ch);
            return true;
        }

        if (ch.MonitorOnly)
        {
            ch.Output = -1;
            ch.Driver = null;
            ch.Status = (ch.Kind == FanKind.Pump ? "Pumpe" : "Lüfter") + " – nur Anzeige: dieser Anschluss lässt sich nicht steuern " +
                        "(läuft mit fester Drehzahl bzw. nach BIOS). Fällt die Drehzahl aus, warnt Toast Temperatur.";
            if (ch.Kind == FanKind.Pump && ch.Rpm?.Value is float pr && pr < 300) ch.Status = "⚠ Pumpe steht oder dreht kaum – Kabel/Pumpe prüfen!";
            return true;
        }

        // Externes Gerät noch nicht verbunden
        if (ch.Ext is { Ready: false } ext)
        {
            ch.Status = ext is OasisFan ? "XMG Oasis: " + (ext.Problem ?? "nicht verbunden") : (ext.Problem ?? "Nicht bereit");
            ch.Output = -1;
            ch.Driver = null;
            return true;
        }

        // ----- Erkennen: einige Sekunden anhalten (oder hochdrehen, wenn er steht) -----
        if (DateTime.Now < ch.IdentifyUntil)
        {
            // Schutz geht vor: wird es während des Anhaltens warm, sofort abbrechen und voll kühlen
            if (ch.IdentifyStop)
            {
                float cpu = Temp(Component.Cpu) ?? 0, gpu = Temp(Component.Hotspot) ?? Temp(Component.Gpu) ?? 0;
                if (cpu >= 85 || gpu >= 100 || (Temp(Component.Gpu) ?? 0) >= 90 || cpu - ch.IdentifyStartCpu >= 8 || gpu - ch.IdentifyStartGpu >= 10)
                {
                    ch.IdentifyStop = false;
                    ch.IdentifyUntil = DateTime.Now.AddSeconds(2);   // kurz voll aufdrehen, dann normal weiter
                    ch.IdentifyResult = "Zum Schutz abgebrochen – die Temperatur stieg beim Anhalten. Der Lüfter läuft wieder.";
                }
            }
            ch.Identifying = true;
            float pct = ch.IdentifyStop ? 0 : 100;
            ch.Output = pct;
            Write(ch, pct);
            if (ch.Rpm?.Value is float r)
            {
                ch.IdentifyMinRpm = Math.Min(ch.IdentifyMinRpm, r);
                ch.IdentifyMaxRpm = Math.Max(ch.IdentifyMaxRpm, r);
            }
            ch.Status = ch.IdentifyStop
                ? $"Erkennen: {(ch.Kind == FanKind.Pump ? "diese Pumpe" : "dieser Lüfter")} wird gerade angehalten – welcher steht still? …"
                : "Erkennen: dieser Lüfter dreht gerade voll hoch …";
            return true;
        }
        if (ch.Identifying)
        {
            ch.Identifying = false;
            float before = ch.IdentifyStartRpm;
            bool hasRpm = before >= 0 && ch.IdentifyMaxRpm > 0 || ch.IdentifyMinRpm < float.MaxValue;
            bool reacted;
            if (ch.IdentifyResult != null) reacted = true;   // zum Schutz abgebrochen
            else if (!hasRpm) { reacted = true; ch.IdentifyResult = "Dieser Anschluss meldet keine Drehzahl – bitte selbst schauen, was sich bewegt hat."; }
            else if (ch.IdentifyStop)
            {
                float min = ch.IdentifyMinRpm;
                reacted = min <= before * 0.6f || min < 300;
                ch.IdentifyResult = reacted
                    ? $"Drehzahl fiel von {before:N0} auf {min:N0} U/min – an diesem Anschluss hängt, was eben stehen geblieben ist."
                    : $"Die Drehzahl blieb bei ca. {min:N0} U/min – dieser Anschluss lässt sich nicht anhalten (z. B. Pumpe mit fester Drehzahl oder BIOS auf „Full Speed“).";
            }
            else
            {
                float max = ch.IdentifyMaxRpm;
                reacted = max >= Math.Max(before, 0) + 300 || max > 600;
                ch.IdentifyResult = reacted
                    ? $"Drehzahl stieg von {Math.Max(before, 0):N0} auf {max:N0} U/min – an diesem Anschluss hängt, was eben hochgedreht hat."
                    : "Keine Reaktion – hier hängt vermutlich nichts, oder der Lüfter meldet keine Drehzahl.";
            }
            // Gar keine Reaktion bei sonst drehendem Lüfter: wird nur überwacht
            if (!reacted && before >= 300)
            {
                ch.NotControllable = true;
                SafeDefault(ch);
            }
            ch.IdentifyFinished = true;
            ch.IdentifyEndedAt = DateTime.Now;
            ch.KickUntil = DateTime.Now.AddSeconds(3);   // nach dem Anhalten kräftig wieder anlaufen
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
                : ch.Kind == FanKind.Pump
                ? "Pumpe läuft mit fester Drehzahl (z. B. 3-Pin-Pumpe) – wird nur überwacht. Im BIOS kann der Anschluss auf „DC“ bzw. „Water Pump“ gestellt werden."
                : "Lässt sich nicht steuern – der Lüfter reagiert nicht auf Befehle. „Erkennen“ testet erneut.";
            return true;
        }

        if (prof.Profile == FanProfile.Bios)
        {
            if (ch.Output >= 0) SafeDefault(ch);
            ch.Status = "Wird vom BIOS / Treiber geregelt";
            return true;
        }
        return ch.Faulted;
    }

    (float Min, float Max) Limits(FanChannel ch, ProfileInfo prof)
    {
        float min = prof.MinPercent;
        if (ch.Kind == FanKind.Pump) min = Math.Max(min, _s.PumpMinPercent);
        else if (ch.Cfg.StartPercent is float sp && prof.Profile != FanProfile.Max) min = Math.Max(min, sp);
        min = Math.Max(min, ch.Control?.MinSoftwareValue ?? 0f);
        float max = Math.Min(100f, ch.Control is { MaxSoftwareValue: > 0 } c ? c.MaxSoftwareValue : 100f);
        return (Math.Min(min, max), max);
    }

    /// Gruppen-Mitglied: übernimmt die Leistung des führenden Lüfters
    void TickFollower(FanChannel ch, ProfileInfo prof)
    {
        if (Prelude(ch, prof)) return;
        var g = GroupOf(ch)!;
        var leader = LeaderOf(g)!;

        if (leader.Output < 0 || leader.Faulted)
        {
            if (ch.Output >= 0) SafeDefault(ch);
            ch.Driver = null;
            ch.Status = $"Gruppe „{g.Name}“ · {leader.DisplayName} wird gerade vom BIOS geregelt – dieser Lüfter auch";
            return;
        }

        var (min, max) = Limits(ch, prof);
        // Während der führende Lüfter per „Erkennen“ auf 100 % läuft, nicht mitziehen
        float src = leader.Identifying ? (ch.Output >= 0 ? ch.Output : leader.Output) : leader.Output;
        float next = Math.Clamp(src, min, max);
        ch.ZeroStopped = leader.ZeroStopped && CanStop(ch);
        if (ch.ZeroStopped) next = 0;
        ch.Output = next;
        ch.Driver = leader.Driver;
        ch.DriverTemp = leader.DriverTemp;
        Write(ch, next);
        ch.Status = $"Gruppe „{g.Name}“ · folgt {leader.DisplayName}" +
                    (leader.Driver is Component d && leader.DriverTemp is float t ? $" (bestimmt von {Components.Name(d)}, {t:0} °C)" : "");
        if (leader.Status.StartsWith("⚠")) ch.Status = "⚠ " + ch.Status;
        if (ch.ZeroStopped) ch.Status = $"Gruppe „{g.Name}“ · steht still wie {leader.DisplayName} (Leerlauf)";
        if (ch.Od8?.LastError is string err) ch.Status += $" · {err}";
        CheckResponse(ch, next);
        if (ch.NotControllable) return;
        StallCheck(ch, next);
    }

    /// Darf dieser Lüfter überhaupt stehen bleiben? Nur kalibrierte Gehäuse-/CPU-Lüfter, keine Pumpen
    public bool CanStop(FanChannel ch) =>
        _s.ZeroRpmEnabled && ch.Cfg.AllowStop && ch.Kind == FanKind.Fan && !ch.NotControllable
        && (ch.IsGpu ? GpuCanStop(ch) : ch.Cfg.StartPercent != null);

    /// Grafikkarte: nur AMD über Overdrive 8 mit Zero-RPM (der Treiber hält die Lüfter selbst sicher an)
    static bool GpuCanStop(FanChannel ch) => ch.Od8 is { LastError: null, CanZeroRpm: true };

    /// Lüfter, die die Voraussetzungen erfüllen (ohne den Schalter)
    public IEnumerable<FanChannel> StopCandidates =>
        Channels.Where(c => c.Kind == FanKind.Fan && !c.NotControllable && (c.IsGpu ? GpuCanStop(c) : c.Cfg.StartPercent != null));

    float ZeroLimit(Component c) => c switch
    {
        Component.Cpu => _s.ZeroCpu,
        Component.Gpu => _s.ZeroGpu,
        _ => _s.BoardLimit - 10,
    };

    bool WantsZero(FanChannel ch, ProfileInfo prof)
    {
        if (!CanStop(ch) || prof.Profile is not (FanProfile.Eco or FanProfile.Balanced))
        {
            ch.QuietSince = null;
            return false;
        }
        var used = Components.Selectable.Where(c => ch.Cfg.Uses(c) && Temp(c) is float).ToList();
        if (used.Count == 0) return false;
        // Hysterese: stehen bleiben, bis es 4 °C über der Grenze ist
        float extra = ch.ZeroStopped ? 4f : 0f;
        bool cool = used.All(c => Temp(c)!.Value < ZeroLimit(c) + extra);
        if (!cool) { ch.QuietSince = null; return false; }
        if (ch.ZeroStopped) return true;
        // Erst nach 30 s Ruhe anhalten, und frühestens 60 s nach dem letzten Anlaufen
        ch.QuietSince ??= DateTime.Now;
        return (DateTime.Now - ch.QuietSince.Value).TotalSeconds >= 30 && (DateTime.Now - ch.RestartedAt).TotalSeconds >= 60;
    }

    string ZeroStatus(FanChannel ch)
    {
        var used = Components.Selectable.Where(c => ch.Cfg.Uses(c) && Temp(c) is float);
        return "Steht still (Leerlauf) · läuft an ab " +
               string.Join(" oder ", used.Select(c => $"{Components.Name(c)} {ZeroLimit(c) + 4:0} °C"));
    }

    void StallCheck(FanChannel ch, float next)
    {
        // Sicherheit (optional): Lüfter dreht trotz Befehl nicht -> zurück ans BIOS
        if (_s.StallDetection && !ch.Cfg.NoTachSignal && ch.Rpm?.Value is float rpm && rpm < 50 && next >= 40)
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

    void TickChannel(FanChannel ch, ProfileInfo prof)
    {
        if (Prelude(ch, prof)) return;

        // ----- Für jedes gewählte Bauteil die nötige Leistung bestimmen, die höchste gewinnt -----
        var targets = _s.TargetsFor(prof.Profile);
        float desired = float.MinValue;   // Smart-Werte dürfen auch unter 0 liegen (Bauteil sehr kühl)
        Component? driver = null;
        float? driverTemp = null;
        float driverTarget = 0;
        bool emergency = false;

        if (ch.Output < 0)
        {
            ch.Output = ch.Readback ?? 50f;
            if (ch.LastTestedPct < 0 && ch.Rpm?.Value is float r0)
            {
                ch.LastTestedPct = ch.Output;
                ch.TestStartRpm = r0;
            }
        }

        foreach (var c in Components.All)
        {
            if (!ch.Cfg.Uses(c) || Temp(c) is not float raw) continue;
            // Grafikkarten-Lüfter: nach dem Hot Spot regeln (heißester Punkt, eigenes Ziel),
            // die normale GPU-Temperatur nur, wenn die Karte keinen Hot Spot meldet
            if (ch.IsGpu && c == Component.Gpu && Temp(Component.Hotspot) is float) continue;
            var st = ch.States[c];
            // Steigt die Temperatur, schnell folgen; fällt sie, langsam (kurze Lastpausen lassen die Lüfter nicht abfallen)
            st.Smooth = st.Smooth is float sm ? sm + (raw > sm ? 0.35f : 0.06f) * (raw - sm) : raw;
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

        // Mainboard-Grenze: Mainboard-Lüfter hochregeln, sobald das Mainboard zu warm wird (+8 % je °C darüber)
        bool boardGuard = false;
        if (!ch.IsGpu && _s.BoardGuardEnabled && prof.Profile != FanProfile.Max
            && Temp(Component.Board) is float bt && bt > _s.BoardLimit)
        {
            float g = Math.Max(prof.MinPercent, 20f) + (bt - _s.BoardLimit) * 8f;
            if (g > desired)
            {
                desired = g;
                driver = Component.Board;
                driverTemp = bt;
                driverTarget = _s.BoardLimit;
                boardGuard = true;
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

        var (min, max) = Limits(ch, prof);
        desired = Math.Clamp(desired, min, max);

        // Sanft ändern: schnell hoch, langsam runter
        // Ruhig einpendeln: schnell hoch; runter erst nach 20 s ohne Anstieg, dann langsam;
        // kleine Abweichungen (unter 2 %) werden ignoriert, damit die Drehzahl nicht ständig schwankt
        float next = ch.Output;
        if (desired > ch.Output + 0.5f)
        {
            next = Math.Min(desired, ch.Output + prof.UpRate);
            ch.LastUpAt = DateTime.Now;
        }
        else if (desired < ch.Output - 2f && (DateTime.Now - ch.LastUpAt).TotalSeconds >= 20)
        {
            next = Math.Max(desired, ch.Output - prof.DownRate);
        }

        string who = Components.Name(driver.Value);
        ch.Status = prof.Profile switch
        {
            FanProfile.Max => "Volle Leistung",
            FanProfile.Custom => $"Eigene Kurve · bestimmt von {who} ({driverTemp:0} °C)",
            _ => $"Smart · bestimmt von {who}: {driverTemp:0} °C, Ziel {driverTarget:0} °C · " +
                 $"{ch.Cfg.LearnedFor(prof.Profile, driver.Value).Count(v => v >= 0)}/11 Laststufen gelernt · " +
                 (ch.Cfg.EffectFor(driver.Value) is float ef
                     ? (ef < WeakEffect ? $"Wirkung gering ({ef:0.0} °C je 10 %) – gedämpft" : $"Wirkung {ef:0.0} °C je 10 %")
                     : "Wirkung wird gemessen"),
        };
        if (boardGuard)
            ch.Status = $"Mainboard-Grenze: {driverTemp:0} °C liegt über {_s.BoardLimit:0} °C – Lüfter erhöht";

        // Sicherheit (optional): zu heiß -> sofort volle Kühlung
        if (emergency)
        {
            next = max;
            ch.Status = "⚠ Notkühlung – Grenzwert erreicht";
        }

        next = Math.Clamp(next, min, max);

        // Lüfter-Stopp im Leerlauf (nur Energiesparend / Ausgewogen, nie bei Notkühlung oder Mainboard-Grenze)
        if (!emergency && !boardGuard && WantsZero(ch, prof))
        {
            if (!ch.ZeroStopped) { ch.ZeroStopped = true; ch.Output = 0; }
            next = 0;
            ch.Status = ZeroStatus(ch);
        }
        else
        {
            if (ch.ZeroStopped)
            {
                // Wieder anlaufen: kurz kräftig anschieben, dann normal regeln
                ch.ZeroStopped = false;
                ch.KickUntil = DateTime.Now.AddSeconds(3);
                ch.RestartedAt = DateTime.Now;
            }
            if (DateTime.Now < ch.KickUntil)
            {
                next = Math.Max(next, Math.Min(max, Math.Max(50f, (ch.Cfg.StartPercent ?? 30f) + 20f)));
                ch.Status = "Läuft wieder an …";
            }
        }

        ch.Output = next;
        Write(ch, next);
        if (ch.Od8?.LastError is string err)
            ch.Status += $" · {err}";
        if (ch.Cfg.Kind == null && (ch.Cfg.DetectedPump || ch.PumpHeader)) ch.Status += " · automatisch als Pumpe erkannt";
        if (ch.Kind == FanKind.Pump && ch.Cfg.StableKey is string pk && !_s.PumpKeys.Contains(pk)) { _s.PumpKeys.Add(pk); _s.Save(); }
        if (ch.Ext == null && ch.Rpm?.Value is float rz && rz < 50 && ch.Output >= 25)
            ch.Status += " · kein Drehzahlsignal – wird trotzdem geregelt, Wirkung über die Temperatur gemessen";
        if (ch.Ext?.Alarm is string alarm) ch.Status = "⚠ " + alarm;
        else if (ch.Ext?.Note is string note) ch.Status += $" · {note}";
        if (GroupOf(ch) is FanGroup grp && MembersOf(grp).Count() > 1)
            ch.Status += $" · führt Gruppe „{grp.Name}“";
        CheckResponse(ch, next);
        if (ch.NotControllable) return;
        StallCheck(ch, next);
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
        if (ch.Od8 == null && ch.Ext == null && ch.ControlSensor?.Value is float rb && rb >= 0 && rb <= 100)
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
        // Kein Drehzahlsignal (zeigt immer 0): Reaktion lässt sich nicht an der Drehzahl ablesen.
        // Wirkung wird stattdessen über die Temperatur gemessen (Smart-Automatik) – nicht als „nicht steuerbar“ werten.
        if (ch.Cfg.NoTachSignal || rpm < 50 && ch.TestStartRpm < 50) { ch.TestStart = null; ch.TestFailures = 0; return; }

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
    // Ab dieser Wirkung (°C pro 10 % Lüfterleistung) gilt ein Bauteil als "reagiert kaum"
    const float WeakEffect = 0.3f;

    float Adaptive(FanChannel ch, Component c, SourceState st, float T, float target, ProfileInfo p, bool isDriver)
    {
        var cfg = ch.Cfg;
        float e = T - target;

        float load = Load(c) ?? 0f;
        int bucket = Math.Clamp((int)(load / 10f), 0, 10);
        var table = cfg.LearnedFor(p.Profile, c);

        // Wie stark kühlt dieser Lüfter dieses Bauteil überhaupt?
        // Nie dämpfen: Grafikkarten-Lüfter bei der eigenen GPU (der kühlt sie sicher), und wenn es
        // deutlich (≥ 5 °C) über dem Ziel ist – dann hat Kühlen Vorrang vor Lautstärke.
        bool weak = cfg.EffectFor(c) is float eff && eff < WeakEffect
                    && !(ch.IsGpu && c is Component.Gpu or Component.Hotspot)
                    && e < 5f;

        if (st.LastBucket != bucket && st.LastBucket >= 0)
            st.Integral *= 0.5f;   // neue Last -> Grundwert übernimmt, Rest neu einregeln
        st.LastBucket = bucket;

        float basePct = LearnedBase(table, bucket, p.MinPercent);
        if (weak) basePct = Math.Min(basePct, p.MinPercent + 15);

        // Ruhezone ±1,5 °C: dort wird nicht nachgeregelt
        if (Math.Abs(e) > 1.5f) st.Integral += cfg.Ki * e;
        // Bauteil reagiert kaum -> darf die Lüfter nicht hochtreiben
        st.Integral = Math.Clamp(st.Integral, -60f, weak ? 8f : 60f);

        float kp = weak ? cfg.Kp * 0.25f : cfg.Kp;
        float output = basePct + kp * e + st.Integral;

        // Anti-Windup
        if ((output >= 100 && e > 0) || (output <= p.MinPercent && e < 0))
            st.Integral -= cfg.Ki * e;

        // --- Lernen 0: Wirkung messen ---
        // (für alle angehakten Bauteile – egal, wer gerade die Drehzahl bestimmt)
        // Nach 60 s bei gleicher Last: Wie viel kühler/wärmer ist das Bauteil, seit sich die Leistung geändert hat?
        var now = DateTime.Now;
        if (st.SnapTime is null || st.SnapBucket != bucket)
        {
            st.SnapTime = now; st.SnapT = T; st.SnapOut = ch.Output; st.SnapBucket = bucket;
        }
        else if ((now - st.SnapTime.Value).TotalSeconds >= 60)
        {
            float dOut = ch.Output - st.SnapOut;
            if (Math.Abs(dOut) >= 8)
            {
                float measured = (st.SnapT - T) / dOut * 10f;
                // Wird es trotz mehr Lüfter wärmer, steigt gerade die Last/Hitze (z. B. Spiel heizt auf) –
                // das sagt nichts über den Lüfter aus und wird nicht gewertet.
                if (measured > 0)
                {
                    measured = Math.Min(measured, 10f);
                    float? old = cfg.EffectFor(c);
                    cfg.Effect[c.ToString()] = old is float o ? o * 0.7f + measured * 0.3f : measured;
                }
            }
            st.SnapTime = now; st.SnapT = T; st.SnapOut = ch.Output; st.SnapBucket = bucket;
        }

        // Gelernt wird nur vom Bauteil, das gerade wirklich die Drehzahl bestimmt
        if (!isDriver) return output;

        // --- Lernen 1: stabiler Zustand -> Grundwert für diese Last merken ---
        if (Math.Abs(e) < 1.5f) st.SteadyTicks++; else st.SteadyTicks = 0;
        if (!weak && st.SteadyTicks >= 20 && ch.Output >= 0)
        {
            table[bucket] = table[bucket] < 0 ? ch.Output : table[bucket] * 0.9f + ch.Output * 0.1f;
            st.Integral *= 0.9f;
        }

        // --- Lernen 1b: lange deutlich unter Ziel -> gelernten Wert wieder senken ---
        if (e < -3) st.BelowTicks++; else st.BelowTicks = 0;
        if (st.BelowTicks >= 60)
        {
            if (table[bucket] >= 0) table[bucket] = Math.Max(p.MinPercent, table[bucket] * 0.93f);
            st.BelowTicks = 0;
        }
        if (weak && table[bucket] > p.MinPercent + 15) table[bucket] = p.MinPercent + 15;

        // --- Lernen 2: Reaktionsstärke anpassen ---
        int sign = e > 1.5f ? 1 : e < -1.5f ? -1 : 0;
        if (sign != 0) st.Signs.Enqueue(sign);
        while (st.Signs.Count > 60) st.Signs.Dequeue();
        int flips = 0;
        int? prev = null;
        foreach (var sg in st.Signs) { if (prev.HasValue && prev != sg) flips++; prev = sg; }
        if (flips > 8)
        {
            cfg.Kp = Math.Max(0.8f, cfg.Kp * 0.85f);
            cfg.Ki = Math.Max(0.03f, cfg.Ki * 0.85f);
            st.Signs.Clear();
        }

        bool saturated = output >= 99 || output <= p.MinPercent + 0.5f;
        if (!weak && Math.Abs(e) > 4 && !saturated) st.FarTicks++; else st.FarTicks = 0;
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
