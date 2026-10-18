using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

static class Program
{
    /// Mit "/tray" gestartet (z. B. per Autostart): direkt unsichtbar im Infobereich
    public static bool StartInTray { get; private set; }

    static MainForm? _form;

    /// Startschritte mitschreiben (%AppData%\TempMonitor\start.log) – zeigt, wo ein Start hängen bleibt
    public static void Step(string what)
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "start.log");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss.fff}  {what}{Environment.NewLine}");
        }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Fehler-Handler ganz zuerst – auch ein Absturz beim Start landet so im Fehler-Logbuch
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            ErrorLog.Write("Unbehandelter Fehler", e.Exception);
            _form?.EmergencyRestore();
            MessageBox.Show(e.Exception.Message + "\n\nDetails stehen im Fehler-Logbuch (Einstellungen).", "Toast Temperatur – Fehler");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ErrorLog.Write("Absturz", e.ExceptionObject as Exception);
            Step("Absturz: " + (e.ExceptionObject as Exception)?.Message);
            _form?.EmergencyRestore();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("Hintergrund-Aufgabe", e.Exception);
            e.SetObserved();
        };

        try
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "start.log");
            if (File.Exists(log)) File.Delete(log);
        }
        catch { }
        Step($"Start v{Updater.CurrentVersion.ToString(3)}");

        // Nur eine Instanz gleichzeitig (wichtig auch für den Updater)
        using var mutex = new Mutex(true, "TempMonitor_SingleInstance", out bool first);
        if (!first) { Step("läuft schon – vorhandenes Fenster öffnen"); SingleInstance.SignalFirst(); return; }

        StartInTray = args.Any(a => a.Equals("/tray", StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();
        try
        {
            _form = new MainForm();
            Step("Fenster aufgebaut");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Start fehlgeschlagen", ex);
            Step("Start fehlgeschlagen: " + ex);
            MessageBox.Show("Toast Temperatur konnte nicht starten:\n\n" + ex.Message +
                            "\n\nDetails: %AppData%\\TempMonitor\\fehler.log", "Toast Temperatur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var form = _form;
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
// Sparmodus: langsame Bauteile (Laufwerke, Netzwerk, RAM) seltener abfragen,
// im Hintergrund ohne aktive Lüftersteuerung alles nur alle 2 Sekunden
// ---------------------------------------------------------------------------
sealed class SmartUpdater
{
    readonly UpdateVisitor _visitor = new();
    readonly Dictionary<IHardware, DateTime> _last = new();

    static bool Slow(HardwareType t) =>
        t is HardwareType.Storage or HardwareType.Network or HardwareType.Memory
        || t.ToString() is "Battery" or "Psu";

    public void Update(IComputer computer, bool hidden, bool needFast)
    {
        var now = DateTime.Now;
        bool save = AppSettings.Instance.PowerSave;
        foreach (var hw in computer.Hardware)
        {
            double every = !save ? 1
                         : Slow(hw.HardwareType) ? (hidden ? 10 : 3)
                         : hidden && !needFast ? 2 : 1;
            if (_last.TryGetValue(hw, out var t) && (now - t).TotalSeconds < every - 0.2) continue;
            _last[hw] = now;
            hw.Accept(_visitor);
        }
    }
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
        IsNetworkEnabled = true,
        IsPsuEnabled = true,
        IsBatteryEnabled = true,
    };

    readonly UpdateVisitor _visitor = new();
    readonly SmartUpdater _updater = new();
    readonly Panel _tempArea;
    readonly HistoryStore _history = new();
    DashboardView? _dash;
    ComponentPage? _page;
    readonly Label _status;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    bool _busy;

    FanController? _fans;
    FanView? _fanView;
    readonly NotifyIcon _tray;
    readonly TabButton _tempTab, _fanTab, _setTab, _anaTab, _rgbTab;
    RgbView? _rgbView;
    RgbService? _rgb;
    SettingsView? _settingsView;
    AnalysisView? _analysis;
    KeySensors? _keys;
    readonly WarningMonitor _warn = new();
    readonly AutoSwitcher _auto = new();
    OverlayForm? _overlay;
    string? _lastError;
    readonly List<(string Short, ISensor Sensor)> _heroes = new();
    readonly DataLogger _logger = new();
    bool _exiting, _shownOnce, _loaded;

    float K => DeviceDpi / 96f;

    public MainForm()
    {
        Text = $"Toast Temperatur {Updater.CurrentVersion.ToString(3)}";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Size = new Size((int)(1260 * K), (int)(880 * K));
        MinimumSize = new Size((int)(520 * K), (int)(360 * K));
        DarkMode.TitleBar(this);

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
            Text = "🌡  Toast Temperatur",
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
        _tempArea = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
        };

        var loading = new Label
        {
            Text = "Sensoren werden gesucht … (das dauert beim ersten Start ein paar Sekunden)",
            ForeColor = Theme.Muted,
            AutoSize = true,
            Font = new Font("Segoe UI", 11f),
            Margin = new Padding((int)(10 * K), (int)(20 * K), 0, 0),
        };
        _tempArea.Controls.Add(loading);

        // ---------- Reiter ----------
        var nav = new Panel
        {
            Dock = DockStyle.Top,
            Height = (int)(44 * K),
            BackColor = Theme.Header,
            Padding = new Padding((int)(14 * K), 0, 0, 0),
        };
        _setTab = new TabButton("Einstellungen") { Dock = DockStyle.Left, Width = (int)(150 * K) };
        _fanTab = new TabButton("Lüfter") { Dock = DockStyle.Left, Width = (int)(130 * K) };
        _anaTab = new TabButton("Analyse") { Dock = DockStyle.Left, Width = (int)(130 * K) };
        _rgbTab = new TabButton("RGB") { Dock = DockStyle.Left, Width = (int)(100 * K), Visible = false };
        _tempTab = new TabButton("Übersicht") { Dock = DockStyle.Left, Width = (int)(140 * K), Selected = true };
        _tempTab.Click += (_, _) => { ShowTab(0); ShowDashboard(); };
        _fanTab.Click += (_, _) => ShowTab(1);
        _setTab.Click += (_, _) => ShowTab(2);
        _anaTab.Click += (_, _) => ShowTab(3);
        _rgbTab.Click += (_, _) => ShowTab(4);
        nav.Controls.Add(_setTab);   // Dock Left: zuletzt hinzugefügt steht ganz links
        nav.Controls.Add(_rgbTab);
        nav.Controls.Add(_anaTab);
        nav.Controls.Add(_fanTab);
        nav.Controls.Add(_tempTab);

        // ---------- Symbol im Infobereich ----------
        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "Toast Temperatur",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        Controls.Add(_tempArea);
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
            Program.Step("Sensoren öffnen");
            await Task.Run(() =>
            {
                _computer.Open();
                _computer.Accept(_visitor);
            });
            Program.Step("Sensoren gelesen");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Sensoren öffnen", ex);
            _status.Text = "Fehler: " + ex.Message;
            return;
        }

        // Lüfter zuerst suchen: liefert Namen und leere Anschlüsse für die Wertetabellen
        try
        {
            Program.Step("Lüfter suchen");
            _fans = new FanController(FanSettings.Load());
            _fans.Discover(_computer);
            FanController.Current = _fans;
            Program.Step($"{_fans.Channels.Count} Lüfter gefunden");
            StartOasis(_fans);
            if (System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            {
                Program.Step("Laptop-Schnittstelle prüfen");
                try
                {
                    if (_fans.Settings.LaptopControl && UniwillEc.Probe() is UniwillEc ec)
                    {
                        _fans.AddUniwill(ec);
                        Program.Step("Uniwill-Laptop-Lüfter eingebunden");
                    }
                    else _fans.Laptop = TongfangLaptop.Probe();
                }
                catch (Exception lex) { ErrorLog.Write("Laptop-Lüfter", lex); }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lüfter suchen", ex);
            _fans = null;
            _status.Text = "Lüftersteuerung nicht verfügbar: " + ex.Message;
        }

        Program.Step("Übersicht aufbauen");
        BuildUi();

        // Lüftersteuerung
        Program.Step("Lüfter-Reiter aufbauen");
        try
        {
            if (_fans == null) throw new InvalidOperationException("keine Lüfter");
            _fanView = new FanView(_fans) { Dock = DockStyle.Fill, Visible = false };
            _fanView.ProfileChanged += UpdateTrayMenu;
            Controls.Add(_fanView);
            _fanView.BringToFront();
            _fanTab.Text = _fans.Channels.Count > 0 ? $"Lüfter ({_fans.Channels.Count})" : "Lüfter";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lüfter-Reiter", ex);
            _fanView = null;
            _status.Text = "Lüfter-Reiter nicht verfügbar: " + ex.Message;
        }
        Program.Step("Einstellungen aufbauen");
        if (_fans is { } fc)
            SettingsView.Extend += v => AutomationSettings.Build(v, fc, _auto);
        try
        {
            _settingsView = new SettingsView(ApplyOverlay, RestartApp) { Dock = DockStyle.Fill, Visible = false };
            Controls.Add(_settingsView);
            _settingsView.BringToFront();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Einstellungen", ex);
            _settingsView = null;
        }
        Program.Step("Analyse aufbauen");
        try
        {
            _analysis = new AnalysisView(_computer, () => _keys, _fans) { Dock = DockStyle.Fill, Visible = false };
            Controls.Add(_analysis);
            _analysis.BringToFront();
        }
        catch (Exception ex)
        {
            _analysis = null;
            ErrorLog.Write("Analyse", ex);
        }
        StartRgb();
        ApplyOverlay();

        UpdateTrayMenu();
        SetupLogger();
        Program.Step("Bereit");
        SingleInstance.Listen(() => { try { BeginInvoke(RestoreFromTray); } catch { } });

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        // Beim Start still nach Updates suchen (fragt nur, wenn es eine neue Version gibt)
        await Updater.CheckAsync(this, manual: false);

        // Danach alle 5 Minuten erneut nachsehen
        _updateTimer.Tick += async (_, _) => await PeriodicUpdateCheck();
        _updateTimer.Start();
        _tray.BalloonTipClicked += async (_, _) =>
        {
            if (_pendingUpdate is not Updater.ReleaseInfo rel) return;
            _pendingUpdate = null;
            RestoreFromTray();
            await Updater.OfferAsync(this, rel);
        };
    }

    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 5 * 60 * 1000 };
    Updater.ReleaseInfo? _pendingUpdate;
    string? _notifiedTag;

    async Task PeriodicUpdateCheck()
    {
        try
        {
            if (_exiting) return;
            var rel = await Updater.PeekAsync();
            if (rel == null) return;
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                await Updater.OfferAsync(this, rel);
            }
            else if (_notifiedTag != rel.Tag)
            {
                // Fenster versteckt (z. B. beim Spielen): nur dezent im Infobereich melden
                _notifiedTag = rel.Tag;
                _pendingUpdate = rel;
                _tray.ShowBalloonTip(10000, "Update verfügbar",
                    $"Toast Temperatur {rel.Version.ToString(3)} ist da – zum Installieren hier klicken.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex) { ErrorLog.Write("Update-Prüfung", ex); }
    }

    // ---------------------------------------------------------------------
    // Reiter, Infobereich (Tray), Beenden
    // ---------------------------------------------------------------------
    /// 0 = Übersicht, 1 = Lüfter, 2 = Einstellungen
    void ShowTab(int tab)
    {
        if (tab == 1 && _fanView == null) tab = 0;
        if (tab == 2 && _settingsView == null) tab = 0;
        if (tab == 3 && _analysis == null) tab = 0;
        if (tab == 4 && _rgbView == null) tab = 0;
        _rgbTab.Selected = tab == 4;
        if (_rgbView != null) _rgbView.Visible = tab == 4;
        _tempTab.Selected = tab == 0;
        _fanTab.Selected = tab == 1;
        _setTab.Selected = tab == 2;
        _anaTab.Selected = tab == 3;
        _tempArea.Visible = tab == 0;
        if (_fanView != null) _fanView.Visible = tab == 1;
        if (_settingsView != null) _settingsView.Visible = tab == 2;
        if (_analysis != null)
        {
            _analysis.Visible = tab == 3;
            if (tab == 3) _analysis.Activate();
        }
        if (tab == 1) _fanView?.RefreshValues();
    }

    /// RGB-Controller des Mainboards im Hintergrund suchen; gefunden -> Reiter "RGB" einblenden
    void StartRgb()
    {
        Task.Run(() =>
        {
            Program.Step("RGB suchen");
            var dev = PolychromeUsb.Probe();
            RgbService svc;
            try { svc = new RgbService(dev, () => _keys); }
            catch (Exception ex) { ErrorLog.Write("RGB", ex); return; }
            void Show()
            {
                try
                {
                    BeginInvoke(() =>
                    {
                        try
                        {
                            bool any;
                            lock (svc.Targets) any = svc.Targets.Count > 0;
                            if (!any || _rgbView != null) return;
                            _rgbView = new RgbView(svc) { Dock = DockStyle.Fill, Visible = false };
                            Controls.Add(_rgbView);
                            _rgbView.BringToFront();
                            _rgbTab.Visible = true;
                        }
                        catch (Exception ex) { ErrorLog.Write("RGB", ex); }
                    });
                }
                catch { }
            }
            _rgb = svc;
            svc.TargetsChanged += Show;   // z. B. OpenRGB oder Oasis später gefunden
            Show();
        });
    }

    /// Overlay passend zur Einstellung ein-/ausblenden
    void ApplyOverlay()
    {
        var s = AppSettings.Instance;
        if (s.OverlayEnabled)
        {
            if (_overlay == null || _overlay.IsDisposed)
            {
                _overlay = new OverlayForm(() => _keys, () => _fans);
                _overlay.Show();
            }
            _overlay.Opacity = Math.Clamp(s.OverlayOpacity, 30, 100) / 100.0;
        }
        else if (_overlay != null && !_overlay.IsDisposed)
        {
            _overlay.Close();
            _overlay = null;
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

        var log = new ToolStripMenuItem("Aufzeichnung aktiv") { Checked = AppSettings.Instance.LogEnabled, CheckOnClick = true };
        log.CheckedChanged += (_, _) => { AppSettings.Instance.LogEnabled = log.Checked; AppSettings.Instance.Save(); };
        menu.Items.Add(log);
        menu.Items.Add("Aufzeichnungen öffnen", null, (_, _) => DataLogger.OpenFolder());

        var ov = new ToolStripMenuItem("Overlay anzeigen") { Checked = AppSettings.Instance.OverlayEnabled };
        ov.Click += (_, _) =>
        {
            AppSettings.Instance.OverlayEnabled = !AppSettings.Instance.OverlayEnabled;
            AppSettings.Instance.Save();
            ApplyOverlay();
        };
        menu.Items.Add(ov);
        menu.Opening += (_, _) => ov.Checked = AppSettings.Instance.OverlayEnabled;

        var auto = new ToolStripMenuItem("Mit Windows starten (im Hintergrund)");
        auto.Click += (_, _) =>
        {
            bool ok = auto.Checked ? Autostart.Disable() : Autostart.Enable();
            if (!ok)
                _tray.ShowBalloonTip(4000, "Autostart", "Autostart konnte nicht geändert werden.", ToolTipIcon.Warning);
        };
        menu.Items.Add(auto);
        // Autostart-Zustand erst beim Öffnen des Menüs prüfen (fragt die Aufgabenplanung)
        menu.Opening += (_, _) => auto.Checked = Autostart.IsEnabled;

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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Das X schließt nicht, sondern versteckt das Fenster: Aufzeichnung und Lüftersteuerung laufen weiter.
        // Beenden nur über Rechtsklick auf das Symbol im Infobereich (oder beim Herunterfahren).
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!AppSettings.Instance.CloseHintShown)
            {
                _tray.ShowBalloonTip(5000, "Toast Temperatur läuft weiter",
                    "Das Programm zeichnet im Hintergrund weiter auf. Öffnen: Doppelklick auf das Symbol. " +
                    "Beenden: Rechtsklick auf das Symbol → Beenden.",
                    ToolTipIcon.Info);
                AppSettings.Instance.CloseHintShown = true;
                AppSettings.Instance.Save();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        EmergencyRestore();
        try { _overlay?.Close(); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        try { _computer.Close(); } catch { }
        base.OnFormClosed(e);
    }

    /// Lüfter sofort zurück an BIOS / Treiber geben
    public void EmergencyRestore()
    {
        try { _fans?.RestoreAll(); } catch { }
        try { OasisLink.Instance?.Shutdown(); } catch { }
        try { _rgb?.Shutdown(); } catch { }
    }

    /// XMG Oasis: auf Laptops (oder wenn schon einmal gefunden) per Bluetooth suchen und einbinden
    void StartOasis(FanController fans)
    {
        var s = fans.Settings;
        bool laptop = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
        if (!s.OasisEnabled || (!laptop && s.OasisAddress == null)) return;
        try
        {
            var link = new OasisLink(s);
            if (s.OasisAddress != null) fans.AddOasis(link);
            link.Found += _ =>
            {
                fans.Save();
                try { BeginInvoke(() => fans.AddOasis(link)); } catch { }
            };
            fans.ChannelsChanged += () =>
            {
                try
                {
                    BeginInvoke(() =>
                    {
                        _fanView?.Rebuild();
                        _fanTab.Text = fans.Channels.Count > 0 ? $"Lüfter ({fans.Channels.Count})" : "Lüfter";
                        SetupLogger();
                        _tray.ShowBalloonTip(4000, "Toast Temperatur", "XMG Oasis gefunden – Lüfter und Pumpe stehen jetzt im Reiter Lüfter.", ToolTipIcon.Info);
                    });
                }
                catch { }
            };
            link.Start();
        }
        catch (Exception ex) { ErrorLog.Write("XMG Oasis", ex); }
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

    internal static string Category(HardwareType t) => t switch
    {
        HardwareType.Cpu => "Prozessor",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "Grafikkarte",
        HardwareType.Motherboard or HardwareType.SuperIO => "Mainboard",
        HardwareType.Storage => "Laufwerk",
        HardwareType.Memory => "Arbeitsspeicher",
        HardwareType.Network => "Netzwerk",
        _ => t.ToString() switch
        {
            "Battery" => "Akku",
            "Psu" => "Netzteil",
            "Cooler" => "Kühlung",
            "EmbeddedController" => "Mainboard (EC)",
            var other => other,
        },
    };

    internal static int Order(HardwareType t) => t switch
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

    /// Die Gesamtauslastung eines Bauteils
    internal static ISensor? MainLoad(IHardware hw)
    {
        var loads = AllSensors(hw).Where(s => s.SensorType == SensorType.Load).ToList();
        if (loads.Count == 0) return null;
        string[] prefs = hw.HardwareType switch
        {
            HardwareType.Cpu => new[] { "CPU Total", "Total" },
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => new[] { "GPU Core", "D3D 3D", "Core" },
            HardwareType.Memory => new[] { "Memory" },
            HardwareType.Storage => new[] { "Total Activity", "Activity" },
            _ => Array.Empty<string>(),
        };
        foreach (var p in prefs)
        {
            var hit = loads.FirstOrDefault(x => x.Name.Equals(p, StringComparison.OrdinalIgnoreCase))
                      ?? loads.FirstOrDefault(x => x.Name.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return loads[0];
    }

    // ---------------------------------------------------------------------
    // Oberfläche aufbauen
    // ---------------------------------------------------------------------
    void BuildUi()
    {
        _history.Record(_computer);
        _keys = KeySensors.From(_computer);

        // Tray-Tooltip: CPU / GPU / Mainboard
        _heroes.Clear();
        foreach (var hw in _computer.Hardware.OrderBy(h => Order(h.HardwareType)))
        {
            string? shortName = hw.HardwareType switch
            {
                HardwareType.Cpu => "CPU",
                HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
                HardwareType.Motherboard => "Board",
                _ => null,
            };
            var temps = AllSensors(hw).Where(IsUsefulTemp).ToList();
            if (shortName != null && temps.Count > 0 && !_heroes.Any(h => h.Short == shortName))
                _heroes.Add((shortName, PickMain(hw, temps)));
        }

        _tempArea.SuspendLayout();
        foreach (var c in _tempArea.Controls.Cast<Control>().ToList()) c.Dispose();
        _tempArea.Controls.Clear();
        _dash = new DashboardView(_computer, _history, OpenPage, CreateBanner) { Dock = DockStyle.Fill };
        _dash.OpenFans = () => BeginInvoke(() => ShowTab(1));
        _tempArea.Controls.Add(_dash);
        _tempArea.ResumeLayout();

        int total = _computer.Hardware.Sum(h => AllSensors(h).Count(s => s.Value.HasValue));
        _status.Text = $"{total} Werte gefunden";
    }

    /// Gelber Hinweis, wenn der Treiber für CPU/Mainboard fehlt (sonst null)
    Control? CreateBanner()
    {
        if (Driver.PawnIoInstalled) return null;
        var banner = new DriverBanner();
        banner.InstallClicked += async () =>
        {
            if (await Driver.InstallAsync(this))
            {
                var r = MessageBox.Show(this,
                    "PawnIO ist installiert. Toast Temperatur muss neu gestartet werden, damit CPU- und Mainboard-Werte erscheinen.\n\nJetzt neu starten?",
                    "Neustart", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes) RestartApp();
            }
        };
        return banner;
    }

    /// Detailseite eines Bauteils öffnen
    void OpenPage(IHardware hw)
    {
        BeginInvoke(() =>
        {
            ShowTab(0);
            _tempArea.SuspendLayout();
            _page?.Dispose();
            _page = new ComponentPage(_computer, hw, _history, ShowDashboard, PinSensor) { Dock = DockStyle.Fill };
            _tempArea.Controls.Add(_page);
            _page.BringToFront();
            if (_dash != null) _dash.Visible = false;
            _tempArea.ResumeLayout();
        });
    }

    void ShowDashboard()
    {
        if (_page == null) return;
        _page.Dispose();
        _page = null;
        if (_dash != null) { _dash.Visible = true; _dash.RefreshValues(); }
    }

    void PinSensor(ISensor s)
    {
        _dash?.AddSensorTile(s);
        _tray.ShowBalloonTip(2500, "Angeheftet", $"„{Hw.SensorLabel(s)}“ ist jetzt in der Übersicht.", ToolTipIcon.Info);
    }

    /// Spalten der Aufzeichnung: alle Temperaturen, die Hauptauslastungen und die Lüfter
    void SetupLogger()
    {
        _logger.Clear();
        var keys = new Dictionary<string, string>();
        void Key(ISensor sensor, string header)
        {
            if (_keys == null) return;
            if (sensor == _keys.Cpu) keys["Cpu"] = header;
            if (sensor == _keys.Gpu) keys["Gpu"] = header;
            if (sensor == _keys.Hotspot) keys["Hotspot"] = header;
            if (sensor == _keys.Board) keys["Board"] = header;
            if (sensor == _keys.CpuLoad) keys["CpuLoad"] = header;
            if (sensor == _keys.GpuLoad) keys["GpuLoad"] = header;
        }
        foreach (var hw in _computer.Hardware.OrderBy(h => Order(h.HardwareType)))
        {
            string cat = Category(hw.HardwareType);
            foreach (var s in AllSensors(hw).Where(IsUsefulTemp))
            {
                var sensor = s;
                Key(sensor, _logger.Add($"{cat} {sensor.Name} [°C]", () => sensor.Value));
            }
            if (MainLoad(hw) is ISensor load)
                Key(load, _logger.Add($"{cat} Auslastung [%]", () => load.Value));
            foreach (var s in AllSensors(hw).Where(x => x.SensorType == SensorType.Power && x.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)))
            {
                var sensor = s;
                string h = _logger.Add($"{cat} {sensor.Name} [W]", () => sensor.Value);
                if (hw.HardwareType == HardwareType.Cpu && !keys.ContainsKey("CpuPower")) keys["CpuPower"] = h;
            }
        }
        if (!keys.OrderBy(k => k.Key).SequenceEqual(AppSettings.Instance.LogKeys.OrderBy(k => k.Key)))
        {
            AppSettings.Instance.LogKeys = keys;
            AppSettings.Instance.Save();
        }
        if (_fans?.Laptop is TongfangLaptop lap)
        {
            _logger.Add("Laptop CPU-Lüfter [U/min]", () => lap.CpuRpm);
            _logger.Add("Laptop GPU-Lüfter [U/min]", () => lap.GpuRpm);
            _logger.Add("Laptop System-Lüfter [U/min]", () => lap.SysRpm);
        }
        if (_fans != null)
        {
            foreach (var ch in _fans.Channels)
            {
                var c = ch;
                if (c.Rpm != null) _logger.Add($"{c.DisplayName} [U/min]", () => c.Rpm?.Value);
                _logger.Add($"{c.DisplayName} [%]", () => c.Output >= 0 ? c.Output : c.Readback);
            }
        }
    }

    /// Programm neu starten (z. B. nach Treiber-Installation)
    void RestartApp()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        _exiting = true;
        Close();
    }

    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var fans = _fans;
            bool hidden = !Visible || WindowState == FormWindowState.Minimized;
            bool needFast = (fans != null && fans.Channels.Count > 0 && (fans.Profile != FanProfile.Bios || fans.Calibrating))
                            || (_analysis?.TestRunning ?? false) || AppSettings.Instance.OverlayEnabled;
            await Task.Run(() =>
            {
                _updater.Update(_computer, hidden, needFast);
                fans?.Tick();
                _history.Record(_computer);
                _logger.Tick();
            });

            // Nur zeichnen, wenn man es sieht – der Verlauf läuft immer weiter
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                if (_tempArea.Visible)
                {
                    if (_page != null) _page.RefreshValues();
                    else _dash?.RefreshValues();
                }
                if (_fanView is { Visible: true }) _fanView.RefreshValues();
            }

            // Automatischer Profilwechsel (Spiele / Zeitplan)
            if (fans is { Channels.Count: > 0 })
            {
                try
                {
                    if (_auto.Update(fans) is string msg)
                    {
                        _fanView?.SyncProfile();
                        UpdateTrayMenu();
                        if (fans.Settings.AutoNotify) _tray.ShowBalloonTip(4000, "Toast Temperatur", msg, ToolTipIcon.Info);
                    }
                }
                catch (Exception aex) { ErrorLog.Write("Profil-Automatik", aex); }
            }

            string prof = fans != null && fans.Channels.Count > 0
                ? $" · Lüfter: {Profiles.Get(fans.Profile).Title}{(fans.Override != null ? " (automatisch)" : "")}" : "";
            string rec = _logger.Enabled ? (_logger.LastError != null ? " · ⚠ Aufzeichnung: " + _logger.LastError : " · ● Aufzeichnung") : "";
            _status.Text = $"Live · {DateTime.Now:HH:mm:ss}{prof}{rec}";

            // Warnungen, Temperatur im Symbol, Overlay
            if (_keys != null)
            {
                try { _warn.Check(_keys, fans, _tray); } catch (Exception wex) { ErrorLog.Write("Warnungen", wex); }
                // Symbol im Infobereich: kann kurz fehlschlagen (z. B. Explorer startet neu) – dann beim nächsten Mal
                try
                {
                    if (AppSettings.Instance.TrayShowTemp)
                    {
                        var ic = TrayIconPainter.Render(_keys.Value(AppSettings.Instance.TrayTempSource), Icon ?? SystemIcons.Application);
                        if (ic != null) _tray.Icon = ic;
                    }
                    else if (_tray.Icon != Icon) _tray.Icon = Icon ?? SystemIcons.Application;
                }
                catch { }
            }
            if (_overlay is { IsDisposed: false }) _overlay.Invalidate();
            try { _rgb?.TickTemperature(); } catch (Exception rex) { ErrorLog.Write("RGB", rex); }
            try { _analysis?.Tick(_analysis.Visible && Visible && WindowState != FormWindowState.Minimized); }
            catch (Exception tex) { ErrorLog.Write("Kühlungs-Test", tex); }

            // Tooltip im Infobereich: wichtigste Temperaturen
            string tip = string.Join("  ", _heroes.Select(h => $"{h.Short} {h.Sensor.Value:0}°"));
            tip = string.IsNullOrEmpty(tip) ? "Toast Temperatur" : tip;
            try { _tray.Text = tip.Length > 63 ? tip[..63] : tip; } catch { }
        }
        catch (Exception ex)
        {
            _status.Text = "Fehler: " + ex.Message;
            if (ex.Message != _lastError) ErrorLog.Write("Aktualisierung", ex);   // gleiche Meldung nur einmal
            _lastError = ex.Message;
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
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
            TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
