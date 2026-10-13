using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

// ===========================================================================
// Profile
// ===========================================================================
enum FanProfile { Bios, Eco, Balanced, Performance, Max, Custom }

sealed record ProfileInfo(
    FanProfile Profile, string Title, string Description, bool Adaptive,
    float CpuTarget, float GpuTarget, float MinPercent, float UpRate, float DownRate);

static class Profiles
{
    // UpRate / DownRate = maximale Änderung in % pro Sekunde (verhindert nerviges Auf- und Abheulen)
    public static readonly ProfileInfo[] All =
    {
        new(FanProfile.Bios,        "Standard (BIOS)",  "Mainboard und Grafikkarte regeln selbst",       false,  0,  0,   0,   0,   0),
        new(FanProfile.Eco,         "Energiesparend",   "So leise wie möglich – Ziel CPU 78 °C",          true,  78, 75,  20,   4,   1),
        new(FanProfile.Balanced,    "Ausgewogen",       "Ruhig und kühl – Ziel CPU 68 °C",                true,  68, 70,  25,   6,   2),
        new(FanProfile.Performance, "Leistung",         "Kühl für Spiele & Rendern – Ziel CPU 58 °C",     true,  58, 62,  35,  10,   2),
        new(FanProfile.Max,         "Maximale Kühlung", "Alle Lüfter dauerhaft auf 100 %",               false,  0,  0, 100, 100, 100),
        new(FanProfile.Custom,      "Eigene Kurve",     "Für jeden Lüfter eine eigene Kurve festlegen",  false,  0,  0,   0,   8,   3),
    };

    public static ProfileInfo Get(FanProfile p) => All.First(i => i.Profile == p);
}

// ===========================================================================
// Einstellungen (werden in %AppData%\TempMonitor\fans.json gespeichert)
// ===========================================================================
sealed class FanChannelSettings
{
    public string? SourceId { get; set; }
    public List<float[]> Curve { get; set; } = DefaultCurve();
    public float Kp { get; set; } = 3.0f;
    public float Ki { get; set; } = 0.15f;
    /// Gelernte Lüfterleistung je Auslastungsstufe (0–10 %, 10–20 % …) pro Profil; -1 = noch unbekannt
    public Dictionary<string, float[]> Learned { get; set; } = new();

    public static List<float[]> DefaultCurve() => new()
    {
        new[] { 30f, 25f }, new[] { 50f, 35f }, new[] { 65f, 55f }, new[] { 75f, 75f }, new[] { 85f, 100f },
    };

    public float[] LearnedFor(FanProfile p)
    {
        string key = p.ToString();
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

    // Sicherheitsfunktionen – standardmäßig aus, in den Einstellungen einschaltbar
    public bool EmergencyEnabled { get; set; }
    public float CpuCritical { get; set; } = 90f;
    public float GpuCritical { get; set; } = 85f;
    public bool StallDetection { get; set; }
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
}

// ===========================================================================
// Ein steuerbarer Lüfter
// ===========================================================================
sealed class FanChannel
{
    public required IControl Control { get; init; }
    public required ISensor ControlSensor { get; init; }
    public ISensor? Rpm { get; init; }
    public ISensor? Load { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string HardwareName { get; init; }
    public bool IsGpu { get; init; }
    public required FanChannelSettings Cfg { get; init; }

    // Laufzeit-Zustand
    public float Output = -1;
    public float? SmoothTemp;
    public float? SourceTemp;
    public float Integral;
    public int LastBucket = -1;
    public int SteadyTicks;
    public int FarTicks;
    public int NoSpinTicks;
    public bool Faulted;
    public string Status = "";
    public readonly Queue<int> Signs = new();
}

// ===========================================================================
// Die eigentliche Steuerung
// ===========================================================================
sealed class FanController
{
    public const string MaxSourceId = "max";

    readonly object _lock = new();
    readonly FanSettings _s;
    DateTime _lastSave = DateTime.Now;

    public List<FanChannel> Channels { get; } = new();
    public List<ISensor> TempSensors { get; } = new();
    public ISensor? CpuMain { get; private set; }
    public ISensor? GpuMain { get; private set; }
    public FanSettings Settings => _s;
    public FanProfile Profile => _s.Profile;

    public FanController(FanSettings settings) => _s = settings;

    // ---------------------------------------------------------------------
    // Lüfter suchen
    // ---------------------------------------------------------------------
    public void Discover(IComputer computer)
    {
        Channels.Clear();
        TempSensors.Clear();

        var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        _cpuLoad = cpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load
                                                    && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase));

        foreach (var hw in computer.Hardware)
        {
            var temps = MainForm.AllSensors(hw).Where(MainForm.IsUsefulTemp).ToList();
            TempSensors.AddRange(temps);
            if (temps.Count == 0) continue;
            if (hw.HardwareType == HardwareType.Cpu && CpuMain == null) CpuMain = MainForm.PickMain(hw, temps);
            if (IsGpu(hw.HardwareType) && GpuMain == null) GpuMain = MainForm.PickMain(hw, temps);
        }

        foreach (var hw in computer.Hardware)
            AddChannels(hw, hw);
    }

    static bool IsGpu(HardwareType t) =>
        t is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    void AddChannels(IHardware top, IHardware hw)
    {
        foreach (var sensor in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null))
        {
            bool gpu = IsGpu(top.HardwareType);
            string baseName = sensor.Name.Replace("Control", "", StringComparison.OrdinalIgnoreCase).Replace("  ", " ").Trim();
            var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
            var rpm = fans.FirstOrDefault(f => f.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                      ?? (gpu ? fans.FirstOrDefault() : null);

            // Auslastung für die vorausschauende Regelung: GPU-Last bzw. CPU-Gesamtlast
            var load = gpu
                ? MainForm.AllSensors(top).FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                : _cpuLoad;

            string id = sensor.Control.Identifier.ToString();
            var cfg = _s.For(id);

            var ch = new FanChannel
            {
                Control = sensor.Control,
                ControlSensor = sensor,
                Rpm = rpm,
                Load = load,
                Id = id,
                Name = rpm?.Name ?? baseName,
                HardwareName = top.Name,
                IsGpu = gpu,
                Cfg = cfg,
            };

            cfg.SourceId ??= gpu ? GpuMain?.Identifier.ToString() : CpuMain?.Identifier.ToString();
            Channels.Add(ch);
        }
        foreach (var sub in hw.SubHardware) AddChannels(top, sub);
    }

    ISensor? _cpuLoad;

    // ---------------------------------------------------------------------
    // Profil wechseln
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
                ch.Integral = 0;
                ch.Signs.Clear();
                ch.SteadyTicks = ch.FarTicks = 0;
                if (p == FanProfile.Bios) SafeDefault(ch);
            }
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
        try { ch.Control.SetDefault(); } catch { }
        ch.Output = -1;
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

    public float? ReadSource(FanChannelSettings cfg)
    {
        if (cfg.SourceId == MaxSourceId)
        {
            float? a = CpuMain?.Value, b = GpuMain?.Value;
            if (a is null) return b;
            if (b is null) return a;
            return Math.Max(a.Value, b.Value);
        }
        var s = TempSensors.FirstOrDefault(x => x.Identifier.ToString() == cfg.SourceId);
        return s?.Value is float v && v > 1 && v < 125 ? v : null;
    }

    void TickChannel(FanChannel ch, ProfileInfo prof)
    {
        float? t = ReadSource(ch.Cfg);
        ch.SourceTemp = t;

        if (prof.Profile == FanProfile.Bios)
        {
            if (ch.Output >= 0) SafeDefault(ch);
            ch.Status = "Wird vom BIOS / Treiber geregelt";
            return;
        }
        if (ch.Faulted) return;

        if (t is not float temp)
        {
            SafeDefault(ch);
            ch.Status = "Kein Temperaturwert – BIOS übernimmt";
            return;
        }

        // Startwert = aktuelle Drehzahl-Einstellung
        if (ch.Output < 0) ch.Output = ch.ControlSensor.Value ?? 50f;

        ch.SmoothTemp = ch.SmoothTemp is float st ? st + 0.35f * (temp - st) : temp;
        float T = ch.SmoothTemp.Value;

        float critical = ch.IsGpu ? _s.GpuCritical : _s.CpuCritical;
        float desired;

        switch (prof.Profile)
        {
            case FanProfile.Max:
                desired = 100;
                ch.Status = "Volle Leistung";
                break;
            case FanProfile.Custom:
                desired = Interpolate(ch.Cfg.Curve, T);
                ch.Status = $"Eigene Kurve · {T:0} °C → {desired:0} %";
                break;
            default:
                desired = Adaptive(ch, T, prof);
                break;
        }

        float min = Math.Max(prof.MinPercent, ch.Control.MinSoftwareValue);
        float max = Math.Min(100f, ch.Control.MaxSoftwareValue > 0 ? ch.Control.MaxSoftwareValue : 100f);
        desired = Math.Clamp(desired, min, max);

        // Sanft ändern: schnell hoch, langsam runter
        float next = desired > ch.Output
            ? Math.Min(desired, ch.Output + prof.UpRate)
            : Math.Max(desired, ch.Output - prof.DownRate);

        // Sicherheit (optional): zu heiß -> sofort volle Kühlung
        if (_s.EmergencyEnabled && temp >= critical)
        {
            next = max;
            ch.Status = $"⚠ Notkühlung – {temp:0} °C";
        }

        next = Math.Clamp(next, min, max);
        ch.Output = next;
        ch.Control.SetSoftware(next);

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
    // Smart-Automatik: lernende Regelung
    //  1) Merkt sich pro Auslastungsstufe, welche Leistung die Zieltemperatur hält
    //     -> reagiert auf Laständerungen sofort (vorausschauend), bevor es heiß wird
    //  2) PI-Regler gleicht den Rest aus
    //  3) Passt die eigene Reaktionsstärke an: schwingt es, wird sie ruhiger;
    //     bleibt es zu lange daneben, wird sie kräftiger
    // ---------------------------------------------------------------------
    float Adaptive(FanChannel ch, float T, ProfileInfo p)
    {
        var cfg = ch.Cfg;
        float target = ch.IsGpu ? p.GpuTarget : p.CpuTarget;
        float e = T - target;

        float load = ch.Load?.Value ?? _cpuLoad?.Value ?? 0f;
        int bucket = Math.Clamp((int)(load / 10f), 0, 10);
        var table = cfg.LearnedFor(p.Profile);

        if (ch.LastBucket != bucket && ch.LastBucket >= 0)
            ch.Integral *= 0.5f;   // neue Last -> Grundwert übernimmt, Rest neu einregeln
        ch.LastBucket = bucket;

        float basePct = LearnedBase(table, bucket, p.MinPercent);

        // PI mit Totzone
        if (Math.Abs(e) > 0.8f) ch.Integral += cfg.Ki * e;
        ch.Integral = Math.Clamp(ch.Integral, -60f, 60f);

        float output = basePct + cfg.Kp * e + ch.Integral;

        // Anti-Windup: an der Grenze nicht weiter aufaddieren
        if ((output >= 100 && e > 0) || (output <= p.MinPercent && e < 0))
            ch.Integral -= cfg.Ki * e;

        // --- Lernen 1: stabiler Zustand -> Grundwert für diese Last merken ---
        if (Math.Abs(e) < 1.5f) ch.SteadyTicks++; else ch.SteadyTicks = 0;
        if (ch.SteadyTicks >= 20 && ch.Output >= 0)
        {
            table[bucket] = table[bucket] < 0 ? ch.Output : table[bucket] * 0.9f + ch.Output * 0.1f;
            ch.Integral *= 0.9f;   // Grundwert trägt jetzt mehr
        }

        // --- Lernen 2: Reaktionsstärke anpassen ---
        int sign = e > 1 ? 1 : e < -1 ? -1 : 0;
        if (sign != 0) ch.Signs.Enqueue(sign);
        while (ch.Signs.Count > 60) ch.Signs.Dequeue();
        int flips = 0;
        int? prev = null;
        foreach (var s in ch.Signs) { if (prev.HasValue && prev != s) flips++; prev = s; }
        if (flips > 8)
        {
            cfg.Kp = Math.Max(0.8f, cfg.Kp * 0.85f);
            cfg.Ki = Math.Max(0.03f, cfg.Ki * 0.85f);
            ch.Signs.Clear();
        }

        bool saturated = output >= 99 || output <= p.MinPercent + 0.5f;
        if (Math.Abs(e) > 4 && !saturated) ch.FarTicks++; else ch.FarTicks = 0;
        if (ch.FarTicks >= 45)
        {
            cfg.Kp = Math.Min(8f, cfg.Kp * 1.1f);
            cfg.Ki = Math.Min(0.6f, cfg.Ki * 1.15f);
            ch.FarTicks = 0;
        }

        int known = table.Count(v => v >= 0);
        ch.Status = $"Smart · Ziel {target:0} °C · {known}/11 Laststufen gelernt";
        return output;
    }

    static float LearnedBase(float[] table, int bucket, float min)
    {
        if (table[bucket] >= 0) return table[bucket];
        // nächsten bekannten Nachbarn nehmen
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
