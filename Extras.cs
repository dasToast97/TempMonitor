using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO.Compression;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

// ===========================================================================
// Fehler-Logbuch: %AppData%\TempMonitor\fehler.log
// ===========================================================================
static class ErrorLog
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "fehler.log");

    static readonly object Lock = new();

    public static void Write(string context, Exception? ex = null)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // nicht endlos wachsen lassen
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".alt", overwrite: true);
                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] v{Updater.CurrentVersion.ToString(3)} · {context}" +
                    (ex != null ? Environment.NewLine + ex : "") + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    public static int Count()
    {
        try { return File.Exists(FilePath) ? File.ReadLines(FilePath).Count(l => l.StartsWith("[")) : 0; }
        catch { return 0; }
    }

    public static void Open()
    {
        try
        {
            if (!File.Exists(FilePath)) Write("Logbuch angelegt");
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
        }
        catch { }
    }
}

// ===========================================================================
// Die wichtigsten Werte an einem Ort (Infobereich, Overlay, Warnungen)
// ===========================================================================
sealed class KeySensors
{
    public ISensor? Cpu, Gpu, Hotspot, Board, CpuLoad, GpuLoad;
    public List<(string Name, ISensor Sensor)> SsdLife { get; } = new();

    public static KeySensors From(IComputer c)
    {
        var k = new KeySensors();
        foreach (var hw in c.Hardware)
        {
            var temps = MainForm.AllSensors(hw).Where(MainForm.IsUsefulTemp).ToList();
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu when k.Cpu == null:
                    if (temps.Count > 0) k.Cpu = MainForm.PickMain(hw, temps);
                    k.CpuLoad = MainForm.MainLoad(hw);
                    break;
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when k.Gpu == null:
                    if (temps.Count > 0) k.Gpu = MainForm.PickMain(hw, temps);
                    k.GpuLoad = MainForm.MainLoad(hw);
                    k.Hotspot = temps.FirstOrDefault(t => t.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                                                       || t.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase)
                                                       || t.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase));
                    break;
                case HardwareType.Motherboard when k.Board == null:
                    if (temps.Count > 0) k.Board = MainForm.PickMain(hw, temps);
                    break;
                case HardwareType.Storage:
                    // Restlebensdauer (SATA) bzw. "Percentage Used" (NVMe)
                    var life = MainForm.AllSensors(hw).FirstOrDefault(s => s.SensorType == SensorType.Level
                        && (s.Name.Contains("Remaining Life", StringComparison.OrdinalIgnoreCase)
                            || s.Name.Contains("Percentage Used", StringComparison.OrdinalIgnoreCase)));
                    if (life != null) k.SsdLife.Add((hw.Name, life));
                    break;
            }
        }
        return k;
    }

    public float? Value(string source) => source switch
    {
        "GPU" => Gpu?.Value,
        "Hotspot" => Hotspot?.Value ?? Gpu?.Value,
        "Max" => Higher(Cpu?.Value, Gpu?.Value),
        _ => Cpu?.Value,
    };

    static float? Higher(float? a, float? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}

// ===========================================================================
// Warnungen als Windows-Benachrichtigung (mit Pause, damit nichts nervt)
// ===========================================================================
sealed class WarningMonitor
{
    readonly Dictionary<string, DateTime> _last = new();
    readonly Dictionary<FanChannel, int> _stopTicks = new();

    public void Check(KeySensors k, FanController? fans, NotifyIcon tray)
    {
        var s = AppSettings.Instance;
        if (!s.WarnEnabled) return;

        void Temp(string key, string name, ISensor? sensor, float limit)
        {
            if (sensor?.Value is float v && v >= limit)
                Notify(key, $"{name} ist heiß: {v:0} °C", $"Grenze: {limit:0} °C. Toast Temperatur beobachtet weiter.", tray);
        }
        Temp("cpu", "CPU", k.Cpu, s.WarnCpu);
        Temp("gpu", "Grafikkarte", k.Gpu, s.WarnGpu);
        Temp("hotspot", "GPU-Hot-Spot", k.Hotspot, s.WarnHotspot);
        Temp("board", "Mainboard", k.Board, s.WarnBoard);

        if (s.WarnFanStop && fans != null && fans.Profile != FanProfile.Bios)
        {
            foreach (var ch in fans.Channels)
            {
                bool stopped = ch.Output >= 40 && ch.Rpm?.Value is float r && r < 50;
                _stopTicks[ch] = stopped ? _stopTicks.GetValueOrDefault(ch) + 1 : 0;
                if (_stopTicks[ch] == 15)
                    Notify("stop|" + ch.Id, $"{ch.DisplayName} steht still",
                        $"Der Lüfter soll mit {ch.Output:0} % laufen, dreht aber nicht. Bitte prüfen.", tray);
            }
        }

        // Externe Geräte (z. B. XMG Oasis: Durchfluss gestört) – immer melden
        if (fans != null)
            foreach (var ch in fans.Channels)
                if (ch.Ext?.Alarm is string al)
                    Notify("ext|" + ch.Id, ch.DisplayName, al, tray);

        if (s.WarnSsd)
        {
            foreach (var (name, sensor) in k.SsdLife)
            {
                if (sensor.Value is not float v) continue;
                bool used = sensor.Name.Contains("Used", StringComparison.OrdinalIgnoreCase);
                float remaining = used ? 100 - v : v;
                if (remaining <= 10)
                    Notify("ssd|" + name, $"{name}: nur noch {remaining:0} % Lebensdauer",
                        "Die SSD nähert sich dem Ende ihrer Lebensdauer. Wichtige Daten sichern.", tray, hours: 24);
            }
        }
    }

    void Notify(string key, string title, string text, NotifyIcon tray, int hours = 0)
    {
        var pause = hours > 0 ? TimeSpan.FromHours(hours) : TimeSpan.FromMinutes(Math.Max(1, AppSettings.Instance.WarnCooldownMinutes));
        if (_last.TryGetValue(key, out var t) && DateTime.Now - t < pause) return;
        _last[key] = DateTime.Now;
        try { tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning); } catch { }
        ErrorLog.Write("Warnung: " + title);
    }
}

// ===========================================================================
// Symbol im Infobereich mit Temperatur als Zahl
// ===========================================================================
static class TrayIconPainter
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    static IntPtr _lastHandle;
    static string _lastText = "";

    public static Icon? Render(float? value, Icon fallback)
    {
        if (value is not float v) return null;
        string text = $"{v:0}";
        if (text == _lastText && _lastHandle != IntPtr.Zero) return null;   // unverändert

        int size = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var col = Theme.ForTemp(v);
            using (var path = Gfx.RoundRect(new Rectangle(0, 0, size - 1, size - 1), size / 4))
            using (var b = new SolidBrush(Color.FromArgb(235, 20, 22, 28)))
                g.FillPath(b, path);
            using (var bar = new SolidBrush(col))
                g.FillRectangle(bar, 0, size - Math.Max(2, size / 8), size, Math.Max(2, size / 8));
            float fs = size * (text.Length >= 3 ? 0.42f : 0.58f);
            using var font = new Font("Segoe UI Semibold", fs, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font);
            using var tb = new SolidBrush(col);
            g.DrawString(text, font, tb, (size - sz.Width) / 2f + 0.5f, (size - sz.Height) / 2f - size * 0.04f);
        }
        IntPtr h = bmp.GetHicon();
        var icon = Icon.FromHandle(h);
        if (_lastHandle != IntPtr.Zero) DestroyIcon(_lastHandle);
        _lastHandle = h;
        _lastText = text;
        return icon;
    }

    public static void Reset() { _lastText = ""; }
}

// ===========================================================================
// Overlay: kleines Fenster, immer obenauf, verschiebbar
// ===========================================================================
sealed class OverlayForm : Form
{
    Font LabelFont => Dpi.F("Segoe UI", 9f, this);
    Font ValueFont => Dpi.F("Segoe UI Semibold", 11f, this);

    readonly Func<KeySensors?> _keys;
    readonly Func<FanController?> _fans;
    Point _dragStart;
    bool _dragging;

    public OverlayForm(Func<KeySensors?> keys, Func<FanController?> fans)
    {
        _keys = keys;
        _fans = fans;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Header;
        DoubleBuffered = true;
        float k = Dpi.K(this);
        Size = new Size((int)(210 * k), (int)(126 * k));
        Opacity = Math.Clamp(AppSettings.Instance.OverlayOpacity, 30, 100) / 100.0;

        var s = AppSettings.Instance;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = s.OverlayX >= 0 && s.OverlayY >= 0 && System.Windows.Forms.SystemInformation.VirtualScreen.Contains(new Point(s.OverlayX, s.OverlayY))
            ? new Point(s.OverlayX, s.OverlayY)
            : new Point(wa.Right - Width - 20, wa.Top + 20);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Overlay ausblenden", null, (_, _) =>
        {
            AppSettings.Instance.OverlayEnabled = false;
            AppSettings.Instance.Save();
            Close();
        });
        ContextMenuStrip = menu;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW: nicht in Alt+Tab
            return cp;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        AppSettings.Instance.OverlayX = Location.X;
        AppSettings.Instance.OverlayY = Location.Y;
        AppSettings.Instance.Save();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = Dpi.K(this);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Header);
        using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var keys = _keys();
        var rows = new List<(string Label, string Value, Color Col)>();
        void Temp(string label, ISensor? t, ISensor? load)
        {
            if (t?.Value is not float v) return;
            string val = $"{v:0}°" + (load?.Value is float l ? $"  ·  {l:0} %" : "");
            rows.Add((label, val, Theme.ForTemp(v)));
        }
        if (keys != null)
        {
            Temp("CPU", keys.Cpu, keys.CpuLoad);
            Temp("GPU", keys.Gpu, keys.GpuLoad);
            Temp("Hot Spot", keys.Hotspot, null);
        }
        if (_fans() is { Channels.Count: > 0 } fc)
        {
            var rpms = fc.Channels.Where(c => c.Kind == FanKind.Fan && !c.IsGpu && c.Rpm?.Value is float).Select(c => c.Rpm!.Value ?? 0).ToList();
            if (rpms.Count > 0) rows.Add(("Lüfter Ø", $"{rpms.Average():N0} U/min", Theme.Text));
            var gpuFan = fc.Channels.FirstOrDefault(c => c.IsGpu)?.Rpm?.Value;
            if (gpuFan is float gf) rows.Add(("GPU-Lüfter", $"{gf:N0} U/min", Theme.Text));
        }

        int y = (int)(8 * k), rh = (int)(22 * k);
        foreach (var (label, value, col) in rows)
        {
            TextRenderer.DrawText(g, label, LabelFont, new Rectangle((int)(10 * k), y, (int)(80 * k), rh), Theme.Muted,
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, value, ValueFont, new Rectangle((int)(80 * k), y, Width - (int)(90 * k), rh), col,
                TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            y += rh;
        }
        int want = y + (int)(6 * k);
        if (Math.Abs(Height - want) > 2) Height = want;
    }
}

// ===========================================================================
// Reiter "Einstellungen"
// ===========================================================================
sealed class SettingsCard : Panel
{
    readonly float _k;
    int _y;
    int S(float px) => (int)(px * _k);

    public SettingsCard(float k)
    {
        _k = k;
        BackColor = Theme.Card;
        _y = S(12);
        Height = S(24);
    }

    void Grow(int h) { _y += h; Height = _y + S(12); }

    public Label Note(string text, bool muted = true)
    {
        var l = new Label
        {
            Text = text, ForeColor = muted ? Theme.Muted : Theme.Text, AutoSize = true, UseMnemonic = false,
            Location = new Point(S(14), _y), MaximumSize = new Size(S(900), 0),
        };
        Controls.Add(l);
        Grow(l.PreferredHeight + S(8));
        return l;
    }

    public CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Text = text, Checked = value, ForeColor = Theme.Text, AutoSize = true, UseMnemonic = false, Location = new Point(S(14), _y) };
        c.CheckedChanged += (_, _) => set(c.Checked);
        Controls.Add(c);
        Grow(S(28));
        return c;
    }

    /// Mehrere Zahlenfelder in einer Zeile: (Beschriftung, Wert, Min, Max, Einheit, Setzen)
    public List<NumericUpDown> Numbers(params (string Label, float Value, int Min, int Max, string Unit, Action<float> Set)[] items)
    {
        var list = new List<NumericUpDown>();
        int x = S(14);
        foreach (var it in items)
        {
            var lbl = new Label { Text = it.Label, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, _y + S(3)) };
            Controls.Add(lbl);
            x += TextRenderer.MeasureText(it.Label, lbl.Font).Width + S(6);
            var n = Ui.Number(it.Value, it.Min, it.Max, new Point(x, _y), _k);
            var set = it.Set;
            n.ValueChanged += (_, _) => set((float)n.Value);
            Controls.Add(n);
            list.Add(n);
            x += n.Width + S(4);
            if (it.Unit.Length > 0)
            {
                var u = new Label { Text = it.Unit, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, _y + S(3)) };
                Controls.Add(u);
                x += TextRenderer.MeasureText(it.Unit, u.Font).Width + S(4);
            }
            x += S(22);
        }
        Grow(S(32));
        return list;
    }

    public void Buttons(params (string Text, Action Click)[] items)
    {
        int x = S(14);
        foreach (var (text, click) in items)
        {
            var b = PageBar.Button(text, _k, Math.Max(150, TextRenderer.MeasureText(text, Font).Width / (int)Math.Max(1, _k) + 40));
            b.Location = new Point(x, _y);
            b.Height = S(30);
            b.Click += (_, _) => click();
            Controls.Add(b);
            x += b.Width + S(10);
        }
        Grow(S(40));
    }

    public ComboBox Combo(string label, string[] items, int selected, Action<int> set)
    {
        var lbl = new Label { Text = label, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(S(14), _y + S(4)) };
        Controls.Add(lbl);
        int x = S(14) + TextRenderer.MeasureText(label, lbl.Font).Width + S(8);
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Theme.CardHero, ForeColor = Theme.Text, Location = new Point(x, _y), Width = S(220),
        };
        c.Items.AddRange(items);
        c.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        c.SelectedIndexChanged += (_, _) => set(c.SelectedIndex);
        Controls.Add(c);
        Grow(S(34));
        return c;
    }

    public void Space(int px) => Grow(S(px));
}

sealed class SettingsView : FlowLayoutPanel
{
    readonly List<Control> _full = new();
    readonly Action _overlayChanged;
    readonly Action _restart;
    float K => Dpi.K(this);
    int S(float px) => (int)(px * K);

    /// Weitere Abschnitte (z. B. Automatik, Gesundheit) hängen sich hier ein
    public static event Action<SettingsView>? Extend;

    public SettingsView(Action overlayChanged, Action restart)
    {
        _overlayChanged = overlayChanged;
        _restart = restart;
        AutoScroll = true;
        BackColor = Theme.Background;
        Padding = new Padding(S(12), S(6), S(12), S(12));
        Resize += (_, _) => ResizeFull();
        DarkMode.Use(this);
        DarkMode.NoHorizontalScroll(this);
        Build();
    }

    public void AddSection(string title, Color accent)
    {
        var h = new SectionHeader(title, accent) { Height = S(40), Margin = new Padding(S(6), S(14), S(6), S(4)) };
        AddFull(h);
    }

    public SettingsCard AddCard()
    {
        var c = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        AddFull(c);
        return c;
    }

    public void AddFullControl(Control c) => AddFull(c);

    void AddFull(Control c)
    {
        _full.Add(c);
        Controls.Add(c);
        SetFlowBreak(c, true);
        ResizeFull();
    }

    void ResizeFull()
    {
        int w = DarkMode.FullWidth(this, S(16));
        foreach (var c in _full) c.Width = w;
    }

    void Build()
    {
        var s = AppSettings.Instance;
        void Save() => s.Save();

        // ----- Warnungen -----
        AddSection("Warnungen", Theme.Hot);
        var w = AddCard();
        w.Check("Windows-Benachrichtigung, wenn etwas zu heiß wird oder ausfällt", s.WarnEnabled, v => { s.WarnEnabled = v; Save(); });
        w.Numbers(
            ("CPU ab", s.WarnCpu, 40, 110, "°C", (Action<float>)(v => { s.WarnCpu = v; Save(); })),
            ("GPU ab", s.WarnGpu, 40, 110, "°C", (Action<float>)(v => { s.WarnGpu = v; Save(); })),
            ("Hot Spot ab", s.WarnHotspot, 40, 120, "°C", (Action<float>)(v => { s.WarnHotspot = v; Save(); })),
            ("Mainboard ab", s.WarnBoard, 30, 100, "°C", (Action<float>)(v => { s.WarnBoard = v; Save(); })));
        w.Check("Warnen, wenn ein gesteuerter Lüfter stehen bleibt", s.WarnFanStop, v => { s.WarnFanStop = v; Save(); });
        w.Check("Warnen, wenn eine SSD das Ende ihrer Lebensdauer erreicht (unter 10 %)", s.WarnSsd, v => { s.WarnSsd = v; Save(); });
        w.Numbers(("Dieselbe Warnung höchstens alle", (float)s.WarnCooldownMinutes, 1, 240, "Minuten", (Action<float>)(v => { s.WarnCooldownMinutes = (int)v; Save(); })));

        // ----- Infobereich & Overlay -----
        AddSection("Infobereich & Overlay", Theme.Accent);
        var t = AddCard();
        string[] sources = { "CPU", "GPU", "Hotspot", "Max" };
        t.Check("Temperatur als Zahl im Symbol unten rechts (Infobereich) anzeigen", s.TrayShowTemp, v => { s.TrayShowTemp = v; TrayIconPainter.Reset(); Save(); });
        t.Combo("Angezeigter Wert:", new[] { "CPU", "Grafikkarte", "GPU-Hot-Spot", "Höchster Wert von CPU und GPU" },
            Array.IndexOf(sources, s.TrayTempSource), i => { s.TrayTempSource = sources[i]; TrayIconPainter.Reset(); Save(); });
        t.Space(6);
        t.Check("Overlay anzeigen: kleines Fenster mit den wichtigsten Werten, immer im Vordergrund (verschiebbar)",
            s.OverlayEnabled, v => { s.OverlayEnabled = v; Save(); _overlayChanged(); });
        t.Numbers(("Deckkraft", (float)s.OverlayOpacity, 30, 100, "%", (Action<float>)(v => { s.OverlayOpacity = (int)v; Save(); _overlayChanged(); })));

        // ----- Sicherung & Diagnose -----
        AddSection("Sicherung & Diagnose", Color.FromArgb(129, 140, 248));
        var bk = AddCard();
        bk.Note("Alle Einstellungen (Lüfter, Pumpen, Profile, RGB, Ansicht) in eine Datei sichern – z. B. vor einer Windows-Neuinstallation " +
                "oder um sie auf einen anderen PC zu übertragen. Das Diagnose-Paket sammelt alle Protokolle für die Fehlersuche.");
        bk.Buttons(("Einstellungen sichern …", () => BackupTools.Export(FindForm())),
                   ("Einstellungen laden …", () => BackupTools.Import(FindForm())),
                   ("Diagnose-Paket erstellen", () => BackupTools.Diagnose(FindForm())));

        // ----- Aufzeichnung & Autostart -----
        AddSection("Aufzeichnung & Autostart", Color.FromArgb(45, 212, 191));
        var a = AddCard();
        a.Check("Werte im Hintergrund aufzeichnen (CSV-Datei pro Tag in Dokumente\\Toast Temperatur)", s.LogEnabled, v => { s.LogEnabled = v; Save(); });
        a.Numbers(("Abstand zwischen zwei Messungen", (float)s.LogIntervalSeconds, 1, 60, "Sekunden", (Action<float>)(v => { s.LogIntervalSeconds = (int)v; Save(); })));
        a.Check("Sparmodus: Laufwerke & Netzwerk seltener abfragen, im Hintergrund ohne Lüftersteuerung nur alle 2 Sekunden messen",
            s.PowerSave, v => { s.PowerSave = v; Save(); });
        a.Check("Automatisch nach Updates suchen (jede Minute; aus = nur beim Start und per Knopf)", s.AutoUpdateCheck,
            v => { s.AutoUpdateCheck = v; Save(); });
        a.Check("Erinnern, wenn die Kühlung nachlässt (Staub / Wärmeleitpaste, höchstens alle 30 Tage)", s.CareReminder,
            v => { s.CareReminder = v; Save(); });
        bool auto = Autostart.IsEnabled;
        a.Check("Mit Windows starten (unsichtbar im Hintergrund)", auto, v =>
        {
            bool ok = v ? Autostart.Enable() : Autostart.Disable();
            if (!ok) MessageBox.Show(FindForm(), "Autostart konnte nicht geändert werden.", "Autostart");
        });
        a.Buttons(("Aufzeichnungen öffnen", DataLogger.OpenFolder));

        // ----- Weitere Abschnitte (Automatik, Gesundheit, Kühlungs-Test …) -----
        if (Extend != null)
            foreach (Action<SettingsView> h in Extend.GetInvocationList())
            {
                try { h(this); }
                catch (Exception ex) { ErrorLog.Write("Einstellungen erweitern", ex); }
            }

        // ----- Fehler-Logbuch -----
        AddSection("Fehler-Logbuch", Theme.Muted);
        var f = AddCard();
        int n = ErrorLog.Count();
        f.Note(n == 0 ? "Bisher keine Einträge." : $"{n} Einträge. Bei Problemen die Datei öffnen und den Inhalt mitschicken.");
        f.Buttons(("Logbuch öffnen", ErrorLog.Open));
    }
}

/// Einstellungen sicher speichern: erst in eine Zwischendatei schreiben (auf die Platte durchgeschrieben),
/// dann austauschen – die vorherige Version bleibt als .bak. So geht bei Absturz/Stromausfall/Herunterfahren
/// während des Speicherns nichts verloren.
static class SafeFile
{
    static readonly object Gate = new();

    /// Nach einem Import: nichts mehr speichern, bis neu gestartet ist (sonst überschreibt der alte Stand den Import)
    public static volatile bool Frozen;

    public static void Write(string path, string text)
    {
        if (Frozen) return;
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true); }
                catch { File.Copy(path, path + ".bak", true); File.Move(tmp, path, true); }
            }
            else File.Move(tmp, path, true);
        }
    }

    /// Text einer gültigen JSON-Datei (Hauptdatei, sonst Sicherung) – null, wenn keine lesbar ist
    public static string? ReadJson<T>(string path) where T : class =>
        Read(path, t => System.Text.Json.JsonSerializer.Deserialize<T>(t) != null ? t : null);

    /// Liest die Datei; ist sie leer/kaputt (parse wirft), wird die Sicherung (.bak) versucht
    public static T? Read<T>(string path, Func<string, T?> parse) where T : class
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var text = File.ReadAllText(p);
                if (string.IsNullOrWhiteSpace(text) || text.Trim('\0').Length == 0) continue;
                var v = parse(text);
                if (v != null)
                {
                    if (p != path) ErrorLog.Write($"Einstellungen aus Sicherung wiederhergestellt: {Path.GetFileName(path)}");
                    return v;
                }
            }
            catch { }
        }
        return null;
    }
}

/// Einstellungen sichern/laden und Diagnose-Paket
static class BackupTools
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");

    public static void Export(IWin32Window? owner)
    {
        try
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Einstellungen sichern",
                Filter = "Toast-Temperatur-Sicherung (*.zip)|*.zip",
                FileName = $"ToastTemperatur-Einstellungen_{DateTime.Now:yyyy-MM-dd}.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return;
            // aktuellen Stand erst auf die Platte schreiben
            try { AppSettings.Instance.Save(); UiState.Instance.Save(); FanController.Current?.Save(); } catch { }
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            using (var zip = System.IO.Compression.ZipFile.Open(dlg.FileName, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var f in Directory.GetFiles(Dir, "*.json"))
                    zip.CreateEntryFromFile(f, Path.GetFileName(f));
            MessageBox.Show(owner, "Gesichert:\n" + dlg.FileName, "Einstellungen sichern", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(owner, "Sichern fehlgeschlagen: " + ex.Message, "Einstellungen sichern", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    public static void Import(IWin32Window? owner)
    {
        try
        {
            using var dlg = new OpenFileDialog { Title = "Einstellungen laden", Filter = "Toast-Temperatur-Sicherung (*.zip)|*.zip" };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return;
            using var zip = System.IO.Compression.ZipFile.OpenRead(dlg.FileName);
            var entries = zip.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && e.FullName == e.Name).ToList();
            if (entries.Count == 0) { MessageBox.Show(owner, "Die Datei enthält keine Einstellungen von Toast Temperatur.", "Einstellungen laden"); return; }
            // Jede Datei vorher prüfen: lesbares JSON?
            foreach (var e in entries)
            {
                using var r = new StreamReader(e.Open());
                System.Text.Json.JsonDocument.Parse(r.ReadToEnd()).Dispose();
            }
            if (MessageBox.Show(owner, $"{entries.Count} Einstellungsdateien laden und die jetzigen ersetzen?\n\n" +
                    "Die jetzigen Einstellungen werden vorher gesichert (Ordner „vor-import“). Toast Temperatur startet danach neu.",
                    "Einstellungen laden", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            SafeFile.Frozen = true;   // der laufende Stand darf den Import nicht mehr überschreiben
            string old = Path.Combine(Dir, "vor-import", DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            Directory.CreateDirectory(old);
            foreach (var f in Directory.GetFiles(Dir, "*.json")) File.Copy(f, Path.Combine(old, Path.GetFileName(f)), true);
            foreach (var e in entries) e.ExtractToFile(Path.Combine(Dir, e.Name), overwrite: true);
            MessageBox.Show(owner, "Geladen. Toast Temperatur startet jetzt neu.", "Einstellungen laden", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Application.Restart();
        }
        catch (Exception ex)
        {
            SafeFile.Frozen = false;
            MessageBox.Show(owner, "Laden fehlgeschlagen: " + ex.Message, "Einstellungen laden", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static void Diagnose(IWin32Window? owner)
    {
        try
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string path = Path.Combine(desk, $"ToastTemperatur-Diagnose_{DateTime.Now:yyyy-MM-dd_HHmm}.zip");
            if (File.Exists(path)) File.Delete(path);
            using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var f in Directory.GetFiles(Dir).Where(f => f.EndsWith(".log") || f.EndsWith(".json") || f.EndsWith(".alt")))
                {
                    try
                    {
                        // Dateien können gerade offen sein -> über einen geteilten Lesezugriff kopieren
                        using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = zip.CreateEntry(Path.GetFileName(f)).Open();
                        src.CopyTo(dst);
                    }
                    catch { }
                }
                var info = zip.CreateEntry("info.txt");
                using var w = new StreamWriter(info.Open());
                w.WriteLine($"Toast Temperatur {Updater.CurrentVersion.ToString(3)}");
                w.WriteLine($"Windows {Environment.OSVersion.Version} · {(Environment.Is64BitOperatingSystem ? "64" : "32")} Bit · {DateTime.Now:yyyy-MM-dd HH:mm}");
            }
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            MessageBox.Show(owner, "Diagnose-Paket liegt auf dem Desktop:\n" + Path.GetFileName(path) +
                "\n\nEs enthält nur Protokolle und Einstellungen von Toast Temperatur – keine persönlichen Dateien.",
                "Diagnose-Paket", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(owner, "Erstellen fehlgeschlagen: " + ex.Message, "Diagnose-Paket", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
