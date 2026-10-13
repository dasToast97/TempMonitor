using System.Drawing.Drawing2D;
using System.Drawing.Text;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

static class Program
{
    /// Mit "/tray" gestartet (z. B. per Autostart): direkt unsichtbar im Infobereich
    public static bool StartInTray { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        // Nur eine Instanz gleichzeitig (wichtig auch für den Updater)
        using var mutex = new Mutex(true, "TempMonitor_SingleInstance", out bool first);
        if (!first) return;

        StartInTray = args.Any(a => a.Equals("/tray", StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();
        var form = new MainForm();

        // Egal wie das Programm endet: Lüfter zurück an das BIOS geben
        Application.ThreadException += (_, e) => { form.EmergencyRestore(); MessageBox.Show(e.Exception.Message, "Fehler"); };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => form.EmergencyRestore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => form.EmergencyRestore();

        Application.Run(form);
    }
}

// ---------------------------------------------------------------------------
// Farben & Einstufung
// ---------------------------------------------------------------------------
static class Theme
{
    public static readonly Color Background = Color.FromArgb(15, 17, 21);
    public static readonly Color Header     = Color.FromArgb(22, 25, 31);
    public static readonly Color Card       = Color.FromArgb(27, 31, 39);
    public static readonly Color CardHero   = Color.FromArgb(31, 36, 46);
    public static readonly Color Border     = Color.FromArgb(46, 52, 64);
    public static readonly Color Grid       = Color.FromArgb(42, 48, 60);
    public static readonly Color Text       = Color.FromArgb(236, 239, 244);
    public static readonly Color Muted      = Color.FromArgb(142, 152, 168);
    public static readonly Color Accent     = Color.FromArgb(96, 165, 250);

    public static readonly Color Cool = Color.FromArgb(74, 222, 128);   // grün
    public static readonly Color Ok   = Color.FromArgb(250, 204, 21);   // gelb
    public static readonly Color Warm = Color.FromArgb(251, 146, 60);   // orange
    public static readonly Color Hot  = Color.FromArgb(248, 113, 113);  // rot

    public static Color ForTemp(float t) => t switch { < 50 => Cool, < 70 => Ok, < 85 => Warm, _ => Hot };
    public static string Status(float t) => t switch { < 50 => "Kühl", < 70 => "Normal", < 85 => "Warm", _ => "Heiß" };

    /// Eigene Farbe je Bauteil – macht die Bereiche auf einen Blick unterscheidbar
    public static Color ForCategory(string cat) => cat switch
    {
        "Prozessor"       => Color.FromArgb(96, 165, 250),   // blau
        "Grafikkarte"     => Color.FromArgb(167, 139, 250),  // lila
        "Mainboard"       => Color.FromArgb(45, 212, 191),   // türkis
        "Laufwerk"        => Color.FromArgb(244, 114, 182),  // pink
        "Arbeitsspeicher" => Color.FromArgb(251, 191, 36),   // bernstein
        _                 => Color.FromArgb(148, 163, 184),
    };
}

// ---------------------------------------------------------------------------
// Aktualisiert alle Hardware-Komponenten inkl. Unterkomponenten (z. B. Super-I/O-Chip)
// ---------------------------------------------------------------------------
sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

// ---------------------------------------------------------------------------
// Hauptfenster
// ---------------------------------------------------------------------------
sealed class MainForm : Form
{
    readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsMemoryEnabled = true,
        IsControllerEnabled = true,
    };

    readonly UpdateVisitor _visitor = new();
    readonly FlowLayoutPanel _flow;
    readonly Label _status;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly List<(ISensor Sensor, SensorCard Card)> _cards = new();
    readonly List<Control> _fullWidth = new();
    bool _busy;

    FanController? _fans;
    FanView? _fanView;
    readonly NotifyIcon _tray;
    readonly TabButton _tempTab, _fanTab;
    readonly List<(string Short, ISensor Sensor)> _heroes = new();
    bool _exiting, _shownOnce, _loaded;

    float K => DeviceDpi / 96f;

    public MainForm()
    {
        Text = $"Temperatur-Monitor {Updater.CurrentVersion.ToString(3)}";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Size = new Size((int)(1260 * K), (int)(880 * K));
        MinimumSize = new Size((int)(520 * K), (int)(360 * K));

        // ---------- Kopfzeile ----------
        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = (int)(56 * K),
            BackColor = Theme.Header,
            Padding = new Padding((int)(18 * K), (int)(10 * K), (int)(18 * K), (int)(10 * K)),
        };
        var title = new Label
        {
            Text = "🌡  Temperatur-Monitor",
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = Theme.Text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var version = new Label
        {
            Text = $"v{Updater.CurrentVersion.ToString(3)}",
            ForeColor = Theme.Muted,
            Dock = DockStyle.Right,
            Width = (int)(60 * K),
            TextAlign = ContentAlignment.MiddleRight,
        };
        var onTop = new CheckBox
        {
            Text = "Immer im Vordergrund",
            ForeColor = Theme.Muted,
            Dock = DockStyle.Right,
            Width = (int)(175 * K),
            TextAlign = ContentAlignment.MiddleRight,
            CheckAlign = ContentAlignment.MiddleRight,
        };
        onTop.CheckedChanged += (_, _) => TopMost = onTop.Checked;

        var updateBtn = new Button
        {
            Text = "⟳  Nach Updates suchen",
            Dock = DockStyle.Right,
            Width = (int)(180 * K),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            Cursor = Cursors.Hand,
        };
        updateBtn.FlatAppearance.BorderColor = Theme.Border;
        updateBtn.FlatAppearance.MouseOverBackColor = Theme.CardHero;
        updateBtn.Click += async (_, _) =>
        {
            updateBtn.Enabled = false;
            try { await Updater.CheckAsync(this, manual: true); }
            finally { updateBtn.Enabled = true; }
        };

        var spacer = new Panel { Dock = DockStyle.Right, Width = (int)(14 * K) };

        top.Controls.Add(title);         // Fill zuerst
        top.Controls.Add(version);
        top.Controls.Add(onTop);
        top.Controls.Add(spacer);
        top.Controls.Add(updateBtn);

        // ---------- Fußzeile mit Legende ----------
        var bottom = new LegendBar { Dock = DockStyle.Bottom, Height = (int)(30 * K) };
        _status = bottom.StatusLabel;

        // ---------- Inhalt ----------
        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Background,
            Padding = new Padding((int)(12 * K), (int)(6 * K), (int)(12 * K), (int)(12 * K)),
        };
        _flow.Resize += (_, _) => ResizeFullWidth();

        var loading = new Label
        {
            Text = "Sensoren werden gesucht … (das dauert beim ersten Start ein paar Sekunden)",
            ForeColor = Theme.Muted,
            AutoSize = true,
            Font = new Font("Segoe UI", 11f),
            Margin = new Padding((int)(10 * K), (int)(20 * K), 0, 0),
        };
        _flow.Controls.Add(loading);

        // ---------- Reiter ----------
        var nav = new Panel
        {
            Dock = DockStyle.Top,
            Height = (int)(44 * K),
            BackColor = Theme.Header,
            Padding = new Padding((int)(14 * K), 0, 0, 0),
        };
        _fanTab = new TabButton("Lüfter") { Dock = DockStyle.Left, Width = (int)(130 * K) };
        _tempTab = new TabButton("Temperaturen") { Dock = DockStyle.Left, Width = (int)(150 * K), Selected = true };
        _tempTab.Click += (_, _) => ShowTab(false);
        _fanTab.Click += (_, _) => ShowTab(true);
        nav.Controls.Add(_fanTab);   // Dock Left: zuletzt hinzugefügt steht ganz links
        nav.Controls.Add(_tempTab);

        // ---------- Symbol im Infobereich ----------
        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "Temperatur-Monitor",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        Controls.Add(_flow);
        Controls.Add(bottom);
        Controls.Add(nav);
        Controls.Add(top);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_loaded) return;   // kann beim Start mit /tray früh aufgerufen werden
        _loaded = true;
        try
        {
            await Task.Run(() =>
            {
                _computer.Open();
                _computer.Accept(_visitor);
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Fehler: " + ex.Message;
            return;
        }

        BuildUi();

        // Lüftersteuerung
        try
        {
            _fans = new FanController(FanSettings.Load());
            _fans.Discover(_computer);
            _fanView = new FanView(_fans) { Dock = DockStyle.Fill, Visible = false };
            _fanView.ProfileChanged += UpdateTrayMenu;
            Controls.Add(_fanView);
            _fanView.BringToFront();
            _fanTab.Text = _fans.Channels.Count > 0 ? $"Lüfter ({_fans.Channels.Count})" : "Lüfter";
        }
        catch (Exception ex)
        {
            _fans = null;
            _status.Text = "Lüftersteuerung nicht verfügbar: " + ex.Message;
        }
        UpdateTrayMenu();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        // Beim Start still nach Updates suchen (fragt nur, wenn es eine neue Version gibt)
        await Updater.CheckAsync(this, manual: false);
    }

    // ---------------------------------------------------------------------
    // Reiter, Infobereich (Tray), Beenden
    // ---------------------------------------------------------------------
    void ShowTab(bool fans)
    {
        _tempTab.Selected = !fans;
        _fanTab.Selected = fans;
        if (fans && _fanView != null)
        {
            _fanView.Visible = true;
            _flow.Visible = false;
            _fanView.RefreshValues();
        }
        else
        {
            _flow.Visible = true;
            if (_fanView != null) _fanView.Visible = false;
        }
    }

    void UpdateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => RestoreFromTray());
        if (_fans != null && _fans.Channels.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var p in Profiles.All)
            {
                var item = new ToolStripMenuItem(p.Title) { Checked = _fans.Profile == p.Profile };
                var prof = p.Profile;
                item.Click += (_, _) => { RestoreFromTray(); _fanView?.SelectProfile(prof); };
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => { _exiting = true; Close(); });
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void SetVisibleCore(bool value)
    {
        // Beim Start mit /tray das Fenster gar nicht erst zeigen
        if (Program.StartInTray && !_shownOnce)
        {
            _shownOnce = true;
            if (!IsHandleCreated) CreateHandle();
            OnLoad(EventArgs.Empty);
            base.SetVisibleCore(false);
            return;
        }
        _shownOnce = true;
        base.SetVisibleCore(value);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Minimieren -> in den Infobereich, damit die Lüftersteuerung weiterläuft
        if (WindowState == FormWindowState.Minimized && _fans != null && _fans.Profile != FanProfile.Bios)
        {
            Hide();
            if (!_fans.Settings.TrayHintShown)
            {
                _tray.ShowBalloonTip(4000, "Läuft weiter",
                    "Die Lüftersteuerung läuft im Infobereich weiter. Doppelklick auf das Symbol öffnet das Fenster.",
                    ToolTipIcon.Info);
                _fans.Settings.TrayHintShown = true;
                _fans.Save();
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Schließen-Knopf bei aktiver Lüftersteuerung = in den Infobereich
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _fans != null && _fans.Profile != FanProfile.Bios)
        {
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        EmergencyRestore();
        _tray.Visible = false;
        _tray.Dispose();
        try { _computer.Close(); } catch { }
        base.OnFormClosed(e);
    }

    /// Lüfter sofort zurück an BIOS / Treiber geben
    public void EmergencyRestore()
    {
        try { _fans?.RestoreAll(); } catch { }
    }

    // ---------------------------------------------------------------------
    // Sensor-Auswahl
    // ---------------------------------------------------------------------
    internal static IEnumerable<ISensor> AllSensors(IHardware hw) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(AllSensors));

    internal static bool IsUsefulTemp(ISensor s) =>
        s.SensorType == SensorType.Temperature
        && s.Value is float v && v > 1 && v < 125
        && !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
        && !s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)
        && !s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
        && !s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase);

    static string Category(HardwareType t) => t switch
    {
        HardwareType.Cpu => "Prozessor",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "Grafikkarte",
        HardwareType.Motherboard or HardwareType.SuperIO => "Mainboard",
        HardwareType.Storage => "Laufwerk",
        HardwareType.Memory => "Arbeitsspeicher",
        HardwareType.Network => "Netzwerk",
        _ => t.ToString(),
    };

    static int Order(HardwareType t) => t switch
    {
        HardwareType.Cpu => 0,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => 1,
        HardwareType.Motherboard => 2,
        HardwareType.Memory => 3,
        HardwareType.Storage => 4,
        _ => 5,
    };

    /// Der "wichtigste" Sensor eines Bauteils für die Übersicht
    internal static ISensor PickMain(IHardware hw, List<ISensor> s)
    {
        string[] prefs = hw.HardwareType switch
        {
            HardwareType.Cpu => new[] { "Package", "Tctl/Tdie", "Tdie", "Tctl", "Core Average", "Core Max" },
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => new[] { "GPU Core", "Core", "Edge" },
            HardwareType.Motherboard => new[] { "System", "Motherboard", "Mainboard", "PCH", "Chipset" },
            _ => Array.Empty<string>(),
        };
        foreach (var p in prefs)
        {
            var hit = s.FirstOrDefault(x => x.Name.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return s.MaxBy(x => x.Value ?? 0)!;
    }

    // ---------------------------------------------------------------------
    // Oberfläche aufbauen
    // ---------------------------------------------------------------------
    void BuildUi()
    {
        _flow.SuspendLayout();
        _flow.Controls.Clear();

        var groups = _computer.Hardware
            .OrderBy(h => Order(h.HardwareType))
            .Select(h => (Hw: h, Sensors: AllSensors(h).Where(IsUsefulTemp).ToList()))
            .Where(g => g.Sensors.Count > 0)
            .ToList();

        if (groups.Count == 0)
        {
            _flow.Controls.Add(new Label
            {
                Text = "Keine Temperatursensoren gefunden.\n\n" +
                       "•  Programm als Administrator starten\n" +
                       "•  Falls CPU/Mainboard fehlen: den Treiber PawnIO installieren (https://pawnio.eu)",
                ForeColor = Theme.Muted,
                AutoSize = true,
                Font = new Font("Segoe UI", 11f),
                Margin = new Padding((int)(10 * K), (int)(20 * K), 0, 0),
            });
            _flow.ResumeLayout();
            _status.Text = "Keine Sensoren";
            return;
        }

        // ===== Übersicht: eine große Kachel pro Bauteil-Art =====
        AddSection("Übersicht", null);
        SensorCard? lastHero = null;
        foreach (var cat in groups.GroupBy(g => Category(g.Hw.HardwareType)))
        {
            // Bei mehreren Laufwerken: das wärmste zeigen
            var g = cat.Count() == 1 ? cat.First()
                : cat.MaxBy(x => PickMain(x.Hw, x.Sensors).Value ?? 0);
            var main = PickMain(g.Hw, g.Sensors);
            var hero = new SensorCard(cat.Key, g.Hw.Name, Theme.ForCategory(cat.Key), big: true);
            hero.Push(main.Value);
            _cards.Add((main, hero));
            _heroes.Add((cat.Key switch
            {
                "Prozessor" => "CPU", "Grafikkarte" => "GPU", "Mainboard" => "Board",
                "Laufwerk" => "SSD", "Arbeitsspeicher" => "RAM", _ => cat.Key,
            }, main));
            _flow.Controls.Add(hero);
            lastHero = hero;
        }
        if (lastHero != null) _flow.SetFlowBreak(lastHero, true);

        // ===== Details: alle Sensoren, nach Bauteil gruppiert =====
        foreach (var (hw, sensors) in groups)
        {
            string cat = Category(hw.HardwareType);
            AddSection($"{cat}  ·  {hw.Name}", Theme.ForCategory(cat));

            SensorCard? last = null;
            foreach (var s in sensors)
            {
                string sub = s.Hardware.Name != hw.Name ? s.Hardware.Name : cat;
                var card = new SensorCard(s.Name, sub, Theme.ForCategory(cat), big: false);
                card.Push(s.Value);
                _cards.Add((s, card));
                _flow.Controls.Add(card);
                last = card;
            }
            if (last != null) _flow.SetFlowBreak(last, true);
        }

        ResizeFullWidth();
        _flow.ResumeLayout();
        _status.Text = $"{_cards.Count - groups.GroupBy(g => Category(g.Hw.HardwareType)).Count()} Sensoren gefunden";
    }

    void AddSection(string text, Color? accent)
    {
        var h = new SectionHeader(text, accent)
        {
            Height = (int)(40 * K),
            Margin = new Padding((int)(6 * K), (int)(14 * K), (int)(6 * K), (int)(4 * K)),
        };
        _fullWidth.Add(h);
        _flow.Controls.Add(h);
        _flow.SetFlowBreak(h, true);
    }

    void ResizeFullWidth()
    {
        int w = Math.Max(100, _flow.ClientSize.Width - _flow.Padding.Horizontal - (int)(16 * K));
        foreach (var c in _fullWidth) c.Width = w;
    }

    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var fans = _fans;
            await Task.Run(() =>
            {
                _computer.Accept(_visitor);
                fans?.Tick();
            });

            if (Visible)
            {
                foreach (var (sensor, card) in _cards) card.Push(sensor.Value);
                if (_fanView is { Visible: true }) _fanView.RefreshValues();
            }
            else
            {
                // Verlauf trotzdem weiterführen, nur nicht neu zeichnen
                foreach (var (sensor, card) in _cards) card.PushSilent(sensor.Value);
            }

            string prof = fans != null && fans.Channels.Count > 0 ? $" · Lüfter: {Profiles.Get(fans.Profile).Title}" : "";
            _status.Text = $"Live · {DateTime.Now:HH:mm:ss}{prof}";

            // Tooltip im Infobereich: wichtigste Temperaturen
            string tip = string.Join("  ", _heroes.Select(h => $"{h.Short} {h.Sensor.Value:0}°"));
            tip = string.IsNullOrEmpty(tip) ? "Temperatur-Monitor" : tip;
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        }
        catch (Exception ex)
        {
            _status.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}

// ---------------------------------------------------------------------------
// Abschnitts-Überschrift mit farbigem Balken
// ---------------------------------------------------------------------------
sealed class SectionHeader : Control
{
    static readonly Font HeadFont = new("Segoe UI Semibold", 12f);
    readonly Color? _accent;

    public SectionHeader(string text, Color? accent)
    {
        Text = text;
        _accent = accent;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = DeviceDpi / 96f;
        g.Clear(Theme.Background);
        int x = 0;
        if (_accent is Color a)
        {
            using var b = new SolidBrush(a);
            g.FillRectangle(b, 0, (int)(Height * 0.28f), (int)(4 * k), (int)(Height * 0.5f));
            x = (int)(12 * k);
        }
        TextRenderer.DrawText(g, Text, HeadFont, new Rectangle(x, 0, Width - x, Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var line = new Pen(Theme.Border);
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}

// ---------------------------------------------------------------------------
// Reiter oben ("Temperaturen" / "Lüfter")
// ---------------------------------------------------------------------------
sealed class TabButton : Control
{
    static readonly Font TabFont = new("Segoe UI Semibold", 10.5f);
    bool _selected, _hover;
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    public TabButton(string text)
    {
        Text = text;
        Cursor = Cursors.Hand;
        BackColor = Theme.Header;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = DeviceDpi / 96f;
        g.Clear(Theme.Header);
        var col = _selected ? Theme.Text : _hover ? Theme.Text : Theme.Muted;
        TextRenderer.DrawText(g, Text, TabFont, new Rectangle(0, 0, Width, Height - (int)(3 * k)), col,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (_selected)
        {
            using var b = new SolidBrush(Theme.Accent);
            g.FillRectangle(b, (int)(12 * k), Height - (int)(3 * k), Width - (int)(24 * k), (int)(3 * k));
        }
    }
}

// ---------------------------------------------------------------------------
// Fußzeile: Farblegende links, Status rechts
// ---------------------------------------------------------------------------
sealed class LegendBar : Panel
{
    public readonly Label StatusLabel;

    public LegendBar()
    {
        BackColor = Theme.Header;
        DoubleBuffered = true;
        StatusLabel = new Label
        {
            Dock = DockStyle.Right,
            AutoSize = false,
            Width = 320,
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 14, 0),
            Text = "Sensoren werden gesucht …",
        };
        Controls.Add(StatusLabel);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float k = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int x = (int)(16 * k);
        int d = (int)(10 * k);
        var items = new (Color C, string T)[]
        {
            (Theme.Cool, "unter 50 °C – kühl"),
            (Theme.Ok,   "50–70 °C – normal"),
            (Theme.Warm, "70–85 °C – warm"),
            (Theme.Hot,  "über 85 °C – heiß"),
        };
        foreach (var (c, t) in items)
        {
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, x, (Height - d) / 2, d, d);
            x += d + (int)(6 * k);
            var sz = TextRenderer.MeasureText(t, Font);
            TextRenderer.DrawText(g, t, Font, new Point(x, (Height - sz.Height) / 2), Theme.Muted);
            x += sz.Width + (int)(18 * k);
        }
    }
}

// ---------------------------------------------------------------------------
// Kachel pro Sensor. "big" = große Übersichtskachel.
// Zeigt Name, aktuellen Wert, Status, Min/Max und Verlaufsgraph (5 Min.)
// Doppelklick setzt Min/Max zurück.
// ---------------------------------------------------------------------------
sealed class SensorCard : Control
{
    const int MaxPoints = 300; // 300 Sekunden = 5 Minuten

    static readonly Font TitleFont   = new("Segoe UI Semibold", 10f);
    static readonly Font HeroTitle   = new("Segoe UI Semibold", 12f);
    static readonly Font SmallFont   = new("Segoe UI", 8f);
    static readonly Font ValueFont   = new("Segoe UI Semibold", 17f);
    static readonly Font HeroValue   = new("Segoe UI Semibold", 30f);
    static readonly Font BadgeFont   = new("Segoe UI Semibold", 8.5f);

    readonly List<float> _hist = new();
    readonly string _title, _subtitle;
    readonly Color _accent;
    readonly bool _big;
    float? _cur;
    float _min = float.MaxValue, _max = float.MinValue;

    public SensorCard(string title, string subtitle, Color accent, bool big)
    {
        _title = title;
        _subtitle = subtitle;
        _accent = accent;
        _big = big;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        Cursor = Cursors.Default;
        new ToolTip().SetToolTip(this, "Doppelklick: Min/Max zurücksetzen");
    }

    protected override void OnCreateControl() { base.OnCreateControl(); ApplySize(); }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); ApplySize(); }

    void ApplySize()
    {
        float k = DeviceDpi / 96f;
        Size = _big ? new Size((int)(292 * k), (int)(214 * k)) : new Size((int)(292 * k), (int)(168 * k));
        Margin = new Padding((int)(6 * k));
    }

    /// Wert in den Verlauf aufnehmen, ohne neu zu zeichnen (Fenster versteckt)
    public void PushSilent(float? value)
    {
        _cur = value;
        if (value is float v)
        {
            _hist.Add(v);
            if (_hist.Count > MaxPoints) _hist.RemoveAt(0);
            _min = Math.Min(_min, v);
            _max = Math.Max(_max, v);
        }
    }

    public void Push(float? value)
    {
        _cur = value;
        if (value is float v)
        {
            _hist.Add(v);
            if (_hist.Count > MaxPoints) _hist.RemoveAt(0);
            _min = Math.Min(_min, v);
            _max = Math.Max(_max, v);
        }
        Invalidate();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        _min = _hist.Count > 0 ? _hist[^1] : float.MaxValue;
        _max = _hist.Count > 0 ? _hist[^1] : float.MinValue;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = DeviceDpi / 96f;
        int S(float px) => (int)(px * k);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        // Kachel-Hintergrund + farbige Kante oben (Bauteil-Farbe)
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = RoundRect(rect, S(12)))
        using (var bg = new SolidBrush(_big ? Theme.CardHero : Theme.Card))
        using (var border = new Pen(Theme.Border))
        {
            g.FillPath(bg, path);
            var oldClip = g.Clip;
            g.SetClip(path);
            using (var acc = new SolidBrush(_accent))
                g.FillRectangle(acc, 0, 0, Width, S(_big ? 4 : 3));
            g.Clip = oldClip;
            g.DrawPath(border, path);
        }

        var col = _cur is float c ? Theme.ForTemp(c) : Theme.Muted;
        const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        Rectangle graph;
        if (_big)
        {
            // Übersichtskachel: Kategorie groß, darunter Bauteilname, dann riesiger Wert + Status
            TextRenderer.DrawText(g, _title, HeroTitle, new Rectangle(S(16), S(12), Width - S(32), S(24)), _accent, L);
            TextRenderer.DrawText(g, _subtitle, SmallFont, new Rectangle(S(16), S(36), Width - S(32), S(16)), Theme.Muted, L);

            string val = _cur is float v ? $"{v:0}°" : "–";
            TextRenderer.DrawText(g, val, HeroValue, new Rectangle(S(12), S(54), S(150), S(52)), col, L);

            if (_cur is float vv) DrawBadge(g, Theme.Status(vv), col, new Point(Width - S(16), S(66)), k);
            if (_max > float.MinValue)
                TextRenderer.DrawText(g, $"min {_min:0}°  ·  max {_max:0}°", SmallFont,
                    new Rectangle(S(100), S(90), Width - S(116), S(16)), Theme.Muted, R);

            graph = new Rectangle(S(16), S(116), Width - S(32), Height - S(130));
            DrawGraph(g, graph, col, k, axis: false);
        }
        else
        {
            TextRenderer.DrawText(g, _title, TitleFont, new Rectangle(S(12), S(10), Width - S(130), S(22)), Theme.Text, L);
            TextRenderer.DrawText(g, _subtitle, SmallFont, new Rectangle(S(12), S(31), Width - S(130), S(16)), Theme.Muted, L);
            TextRenderer.DrawText(g, _cur is float v ? $"{v:0.0} °C" : "–", ValueFont,
                new Rectangle(Width - S(130), S(8), S(118), S(30)), col, R);
            if (_max > float.MinValue)
                TextRenderer.DrawText(g, $"min {_min:0} · max {_max:0}", SmallFont,
                    new Rectangle(Width - S(160), S(36), S(148), S(14)), Theme.Muted, R);

            graph = new Rectangle(S(34), S(58), Width - S(46), Height - S(78));
            DrawGraph(g, graph, col, k, axis: true);
        }
    }

    void DrawBadge(Graphics g, string text, Color col, Point rightTop, float k)
    {
        var sz = TextRenderer.MeasureText(text, BadgeFont);
        var r = new Rectangle(rightTop.X - sz.Width - (int)(16 * k), rightTop.Y, sz.Width + (int)(16 * k), (int)(22 * k));
        using (var p = RoundRect(r, r.Height / 2))
        using (var b = new SolidBrush(Color.FromArgb(40, col)))
            g.FillPath(b, p);
        TextRenderer.DrawText(g, text, BadgeFont, r, col,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    void DrawGraph(Graphics g, Rectangle gr, Color col, float k, bool axis)
    {
        int S(float px) => (int)(px * k);
        if (_hist.Count == 0 || gr.Width < 10 || gr.Height < 10) return;

        float dMin = _hist.Min(), dMax = _hist.Max();
        float lo = (float)Math.Floor((dMin - 3) / 10f) * 10f;
        float hi = (float)Math.Ceiling((dMax + 3) / 10f) * 10f;
        if (lo < 0 && dMin >= 0) lo = 0;
        if (hi - lo < 20) hi = lo + 20;

        const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        using (var gridPen = new Pen(Theme.Grid) { DashStyle = DashStyle.Dot })
        {
            const int steps = 4;
            for (int i = 0; i <= steps; i++)
            {
                float val = lo + (hi - lo) * i / steps;
                float y = gr.Bottom - gr.Height * i / (float)steps;
                if (axis || i == 0 || i == steps) g.DrawLine(gridPen, gr.Left, y, gr.Right, y);
                if (axis)
                    TextRenderer.DrawText(g, $"{val:0}", SmallFont,
                        new Rectangle(0, (int)y - S(7), gr.Left - S(5), S(14)), Theme.Muted, R);
            }
        }
        if (axis)
        {
            TextRenderer.DrawText(g, "−5 min", SmallFont, new Rectangle(gr.Left, gr.Bottom + S(3), S(60), S(14)), Theme.Muted, L);
            TextRenderer.DrawText(g, "jetzt", SmallFont, new Rectangle(gr.Right - S(60), gr.Bottom + S(3), S(60), S(14)), Theme.Muted, R);
        }

        float dx = gr.Width / (float)(MaxPoints - 1);
        var pts = new PointF[_hist.Count];
        for (int i = 0; i < _hist.Count; i++)
        {
            float x = gr.Right - (_hist.Count - 1 - i) * dx;
            float y = gr.Bottom - (_hist[i] - lo) / (hi - lo) * gr.Height;
            pts[i] = new PointF(x, y);
        }

        if (pts.Length >= 2)
        {
            var poly = new PointF[pts.Length + 2];
            pts.CopyTo(poly, 0);
            poly[^2] = new PointF(pts[^1].X, gr.Bottom);
            poly[^1] = new PointF(pts[0].X, gr.Bottom);

            using var fill = new LinearGradientBrush(new Rectangle(gr.X, gr.Y - 1, gr.Width, gr.Height + 2),
                Color.FromArgb(90, col), Color.FromArgb(6, col), LinearGradientMode.Vertical);
            g.FillPolygon(fill, poly);
            using var line = new Pen(col, (_big ? 2.5f : 2f) * k) { LineJoin = LineJoin.Round };
            g.DrawLines(line, pts);
        }

        var p = pts[^1];
        using (var dot = new SolidBrush(col))
            g.FillEllipse(dot, p.X - 3.5f * k, p.Y - 3.5f * k, 7f * k, 7f * k);
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
