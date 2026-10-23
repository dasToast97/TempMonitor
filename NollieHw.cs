using System.Text;

namespace TempMonitor;

// ===========================================================================
// Nollie-Speicher: Beleuchtung, die der Nollie selbst abspielt – beim Hochfahren, im Energiesparmodus und
// immer dann, wenn Toast Temperatur nicht läuft (ca. 3 s nach dem letzten Bild übernimmt der Nollie).
// Nur die Effekt-Einstellungen der Nollie-Firmware (OS2) werden geschrieben – nie Firmware, Bootloader oder Flash-Bereiche.
// Protokoll: OpenNollie (docs/PROTOCOL.md, Nollie-Firmware 2.3.0), auf dem Nollie 8 mit 64-Byte-Berichten geprüft (nur lesend).
// ===========================================================================
sealed class NollieFx
{
    public int Effect { get; set; } = 12;     // Index der eingebauten Effekte
    public int Brightness { get; set; } = 100; // 0–100
    public int Step { get; set; } = 3;          // Tempo 1–10
    public int Size { get; set; } = 10;         // Größe 0–50
    public int Delay { get; set; } = 0;         // Verzögerung 0–8
    public int Color1 { get; set; } = Color.FromArgb(0, 120, 255).ToArgb();
    public int Color2 { get; set; } = Color.FromArgb(160, 0, 255).ToArgb();
    public int Color3 { get; set; } = Color.FromArgb(0, 255, 160).ToArgb();
    public NollieFx Clone() => (NollieFx)MemberwiseClone();
}

sealed class NollieHwSettings
{
    /// Beleuchtung im Nollie speichern (aus = Toast Temperatur schreibt nichts in den Nollie)
    public bool Enabled { get; set; }
    /// 0 = passend zum eigenen Effekt (automatisch), 1 = eigener Nollie-Effekt, 2 = aus
    public int Source { get; set; }
    /// Eigener Nollie-Effekt: jeder Kanal eigene Einstellungen (wie die Canvas in NollieRGB)
    public bool PerChannel { get; set; }
    public NollieFx All { get; set; } = new();
    public List<NollieFx> Channels { get; set; } = new();
    /// Start-Effekt beim Einschalten (-1 = keiner)
    public int BootEffect { get; set; } = -1;
    public int BootColor { get; set; } = Color.FromArgb(0, 120, 255).ToArgb();
    public int BootStep { get; set; } = 3;
    /// Sekunden, bis der Nollie nach dem letzten Bild von Toast Temperatur selbst übernimmt (0 = Standard, ca. 3 s)
    public int IdleSeconds { get; set; }
    /// Zuletzt geschriebener Stand (es wird nur bei Änderungen geschrieben – schont den Speicher)
    public string LastWritten { get; set; } = "";

    public NollieFx For(int ch)
    {
        while (Channels.Count <= ch) Channels.Add(All.Clone());
        return Channels[ch];
    }
}

static class NollieHw
{
    public static readonly string[] EffectNames =
    {
        "Aus", "Farbe (statisch)", "Atmen", "Scannen", "Regenbogen", "Regenbogen-Zyklus", "Meteor", "Zittern",
        "Langsam wischen", "Farbe wischen", "Regenbogen-Streifen", "Regenbogen-Streifen lang", "Welle", "Sonnig",
        "Blüte", "Stapeln 1", "Stapeln 2", "Stapeln 3", "Verschmelzen 1", "Verschmelzen 2", "Wechseln", "Geschosse", "Abfolge",
    };
    public static readonly string[] BootNames =
    {
        "Aufleuchten", "Farbe wischen", "Langsam wischen", "Blüte", "Stapeln 1", "Stapeln 2", "Stapeln 3", "Geschosse",
    };

    /// Welche Einstellungen ein eingebauter Effekt nutzt (aus dem Effekt-Katalog von NollieRGB)
    public static (bool Colors1, bool Color2, bool Color3, bool Step, bool Size, bool Delay) Uses(int fx) => fx switch
    {
        0 => (false, false, false, false, false, false),
        1 => (true, false, true, false, false, false),
        2 => (true, false, true, true, false, true),
        3 or 7 or 21 or 22 => (true, false, true, true, true, true),
        4 => (false, false, true, true, false, true),
        5 or 10 or 11 => (false, false, true, true, true, true),
        6 => (true, true, true, true, true, true),
        12 or 13 or 14 => (true, false, true, true, true, true),
        _ => (true, true, true, true, true, true),   // Wischen, Stapeln, Verschmelzen, Wechseln
    };

    /// Eigenen Toast-Effekt auf den ähnlichsten eingebauten Nollie-Effekt abbilden
    public static NollieFx FromCustom(CustomEffect fx)
    {
        var pal = fx.Colors.Select(Color.FromArgb).ToList();
        if (pal.Count == 0) pal.Add(Color.FromArgb(0, 120, 255));
        Color P(int i) => pal[Math.Min(i, pal.Count - 1)];
        bool calm = fx.Type is EffectType.LavaLamp or EffectType.Tide or EffectType.Aurora or EffectType.Ember or EffectType.Fireflies;
        int effect = fx.Type switch
        {
            EffectType.Breathing or EffectType.Ember => 2,
            EffectType.RainbowWave => 10,
            EffectType.ColorCycle => 5,
            EffectType.Comet => 6,
            EffectType.Sparkle or EffectType.Fireflies => 7,
            EffectType.Fire => 13,
            EffectType.Gradient when Math.Clamp(fx.Speed, 0, 100) < 5 => 1,
            _ => 12,   // Welle, Lavalampe, Meeresrauschen, Nordlicht, Temperatur-Welle
        };
        if (fx.Type == EffectType.TempWave) pal = new() { Color.FromArgb(40, 120, 255), Color.FromArgb(40, 220, 90) };
        float sp = Math.Clamp(fx.Speed, 0, 100) / 100f * (calm ? 0.5f : 1f);
        return new NollieFx
        {
            Effect = effect,
            Brightness = Math.Clamp(fx.Brightness, 0, 100),
            Step = Math.Clamp((int)Math.Round(1 + sp * 9), 1, 10),
            Size = Math.Clamp(fx.Width, 0, 50),
            Delay = 0,
            Color1 = P(0).ToArgb(),
            Color2 = (pal.Count > 2 ? P(2) : Color.FromArgb(10, 10, 30)).ToArgb(),
            Color3 = P(1).ToArgb(),
        };
    }

    const byte SetEffect = 0xFA, SetCore = 0xF6;
    const byte ItemParams = 0x02, ItemMap = 0x03, ItemLen = 0x04, ItemStartLen = 0x05, ItemLedNum = 0x08,
               ItemStartMap = 0x0B, ItemBoot = 0x13, CoreIdle = 0x16;
    const int FwChannels = 16, MapLen = 32, BootCanvas = 14;

    static byte[] Rgb(int argb) { var c = Color.FromArgb(argb); return new[] { c.R, c.G, c.B }; }

    static byte[] Params(int canvas, NollieFx f, int chCount) => new byte[]
    {
        SetEffect, ItemParams, (byte)canvas, (byte)Math.Clamp(f.Effect, 0, 23), (byte)Math.Clamp(f.Brightness, 0, 100),
        (byte)Math.Clamp(f.Step, 1, 10), (byte)Math.Clamp(f.Size, 0, 50), (byte)chCount, 1, (byte)Math.Clamp(f.Delay, 0, 8),
    }.Concat(Rgb(f.Color1)).Concat(Rgb(f.Color2)).Concat(Rgb(f.Color3)).ToArray();

    static byte[] Map(byte item, IEnumerable<int> used)
    {
        var u = used.ToList();
        var flat = u.Concat(Enumerable.Range(0, FwChannels).Where(c => !u.Contains(c))).Select(c => (byte)c).ToList();
        while (flat.Count < MapLen) flat.Add(0);
        return new byte[] { SetEffect, item, 0 }.Concat(flat).ToArray();
    }

    /// Alle Befehle für den gewünschten Stand (Reihenfolge wie NollieRGB: LED-Zahlen, Effekte, Kanal-Zuordnung, Anzahl, Start, Leerlaufzeit)
    public static List<byte[]> Build(NollieHwSettings s, CustomEffect custom, IReadOnlyList<int> leds, Func<int, CustomEffect>? perChannel = null)
    {
        var cmds = new List<byte[]>();
        var active = Enumerable.Range(0, NollieTarget.Channels).Where(c => c < leds.Count && leds[c] > 0).ToList();
        if (active.Count == 0) return cmds;

        // LED-Zahl je Kanal (16 × u16, Kanäle 9–16 gibt es am Nollie 8 nicht)
        var led = new List<byte> { SetEffect, ItemLedNum };
        for (int c = 0; c < FwChannels; c++)
        {
            int n = c < NollieTarget.Channels && c < leds.Count ? Math.Clamp(leds[c], 0, NollieTarget.MaxLeds) : 0;
            led.Add((byte)(n & 0xFF)); led.Add((byte)(n >> 8));
        }
        cmds.Add(led.ToArray());

        // Effekte: eine „Canvas“ für alle Kanäle oder je Kanal eine eigene
        var canvases = new List<(List<int> Chans, NollieFx Fx)>();
        if (s.Source == 2) canvases.Add((active, new NollieFx { Effect = 0, Brightness = 0 }));
        else if (s.Source == 0)
        {
            // Automatisch: jeder Kanal wie sein Effekt in Toast Temperatur (Gruppen) – gleiche Effekte teilen sich eine Canvas
            foreach (var grp in active.GroupBy(ch => Convert.ToHexString(Params(0, FromCustom(perChannel?.Invoke(ch) ?? custom), 0))))
            {
                var chans = grp.ToList();
                canvases.Add((chans, FromCustom(perChannel?.Invoke(chans[0]) ?? custom)));
            }
        }
        else if (!s.PerChannel) canvases.Add((active, s.All));
        else foreach (int c in active) canvases.Add((new List<int> { c }, s.For(c)));
        for (int i = 0; i < canvases.Count; i++) cmds.Add(Params(i, canvases[i].Fx, canvases[i].Chans.Count));
        cmds.Add(Map(ItemMap, canvases.SelectMany(x => x.Chans)));
        cmds.Add(new byte[] { SetEffect, ItemLen, (byte)canvases.Count });

        // Start-Effekt beim Einschalten
        if (s.BootEffect >= 0)
        {
            var bf = new NollieFx { Effect = Math.Clamp(s.BootEffect, 0, 7), Brightness = 100, Step = s.BootStep, Size = 10, Color1 = s.BootColor, Color2 = s.BootColor, Color3 = s.BootColor };
            cmds.Add(Params(BootCanvas, bf, active.Count));
            cmds.Add(Map(ItemStartMap, active));
            cmds.Add(new byte[] { SetEffect, ItemStartLen, 1 });
            cmds.Add(new byte[] { SetEffect, ItemBoot, 0, 0 });   // erst Start-Effekt, dann die gespeicherten Effekte
        }
        else
        {
            cmds.Add(new byte[] { SetEffect, ItemStartLen, 0 });
            cmds.Add(new byte[] { SetEffect, ItemBoot, 1, 0 });   // kein Start-Effekt, gleich die gespeicherten Effekte
        }

        int idle = Math.Clamp(s.IdleSeconds, 0, 3600);
        cmds.Add(new byte[] { SetCore, CoreIdle, (byte)(idle & 0xFF), (byte)(idle >> 8) });
        return cmds;
    }

    public static string Signature(List<byte[]> cmds)
    {
        var sb = new StringBuilder();
        foreach (var c in cmds) sb.Append(Convert.ToHexString(c)).Append('|');
        return sb.ToString();
    }

    public static string Describe(NollieHwSettings s, CustomEffect custom)
    {
        if (!s.Enabled) return "Aus – der Nollie behält, was zuletzt in ihm gespeichert wurde (z. B. über NollieRGB).";
        string Fx(NollieFx f) => EffectNames[Math.Clamp(f.Effect, 0, EffectNames.Length - 1)];
        string main = s.Source switch
        {
            2 => "Beleuchtung aus",
            0 => $"„{Fx(FromCustom(custom))}“ (passend zu deinem Effekt)",
            _ => s.PerChannel ? "eigene Effekte je Kanal" : $"„{Fx(s.All)}“",
        };
        return "Ohne Toast Temperatur zeigt der Nollie: " + main + (s.BootEffect >= 0 ? $", beim Einschalten zuerst „{BootNames[Math.Clamp(s.BootEffect, 0, 7)]}“." : ".");
    }
}
