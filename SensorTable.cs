using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Net.Http.Headers;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace TempMonitor;

// ===========================================================================
// Merkt sich, welche Bereiche auf- oder zugeklappt sind (%AppData%\TempMonitor\ui.json)
// ===========================================================================
sealed class UiState
{
    public Dictionary<string, bool> Expanded { get; set; } = new();
    public List<TileConfig> Tiles { get; set; } = new();
    public bool TilesInitialized { get; set; }
    public List<string> HiddenSensors { get; set; } = new();
    /// Bauteile, deren Sensoren direkt in der Übersicht (unter den Kacheln) stehen
    public List<string> DashboardSections { get; set; } = new();

    public bool IsHidden(string id) => HiddenSensors.Contains(id);

    public void ToggleHidden(string id)
    {
        if (!HiddenSensors.Remove(id)) HiddenSensors.Add(id);
        Save();
    }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "ui.json");

    public static UiState Instance { get; } = Load();

    static UiState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    public bool IsExpanded(string key, bool def) => Expanded.TryGetValue(key, out var v) ? v : def;
    public void Set(string key, bool v) { Expanded[key] = v; Save(); }
}

// ===========================================================================
// Einheiten & Namen der Sensorarten (wie bei HWMonitor)
// ===========================================================================
static class SensorFormat
{
    // Über den Namen statt das Enum, damit auch neuere/ältere Bibliotheksversionen passen
    static readonly (string Type, string Label)[] Order =
    {
        ("Temperature", "Temperaturen"),
        ("Voltage", "Spannungen"),
        ("Power", "Leistung"),
        ("Current", "Ströme"),
        ("Clock", "Takt"),
        ("Load", "Auslastung"),
        ("Fan", "Lüfter"),
        ("Control", "Lüfter-Steuerung"),
        ("Frequency", "Frequenzen"),
        ("Flow", "Durchfluss"),
        ("Level", "Füllstand / Abnutzung"),
        ("Factor", "Faktoren"),
        ("Data", "Daten"),
        ("SmallData", "Speicher"),
        ("Throughput", "Datenrate"),
        ("TimeSpan", "Zeit"),
        ("Timing", "Timings"),
        ("Energy", "Energie"),
        ("Noise", "Lautstärke"),
        ("Humidity", "Luftfeuchtigkeit"),
        ("Conductivity", "Leitfähigkeit"),
    };

    public static int Rank(SensorType t)
    {
        string n = t.ToString();
        for (int i = 0; i < Order.Length; i++) if (Order[i].Type == n) return i;
        return 99;
    }

    public static string Label(SensorType t)
    {
        string n = t.ToString();
        foreach (var (type, label) in Order) if (type == n) return label;
        return n;
    }

    public static string Value(SensorType t, float? value)
    {
        if (value is not float v || float.IsNaN(v)) return "–";
        return t.ToString() switch
        {
            "Temperature" => $"{v:0.0} °C",
            "Voltage" => $"{v:0.000} V",
            "Power" => $"{v:0.0} W",
            "Current" => $"{v:0.000} A",
            "Clock" => $"{v:0} MHz",
            "Load" => $"{v:0.0} %",
            "Fan" => $"{v:N0} U/min",
            "Control" => $"{v:0} %",
            "Frequency" => $"{v:0.0} Hz",
            "Flow" => $"{v:0.0} L/h",
            "Level" => $"{v:0.0} %",
            "Factor" => $"{v:0.000}",
            "Data" => $"{v:0.0} GB",
            "SmallData" => $"{v:N0} MB",
            "Throughput" => v >= 1024 * 1024 ? $"{v / 1024 / 1024:0.0} MB/s" : $"{v / 1024:0.0} KB/s",
            "TimeSpan" => TimeSpan.FromSeconds(v).ToString(@"d\.hh\:mm\:ss"),
            "Timing" => $"{v:0.0} ns",
            "Energy" => $"{v:N0} mWh",
            "Noise" => $"{v:0.0} dBA",
            "Humidity" => $"{v:0.0} %",
            "Conductivity" => $"{v:0.0} µS/cm",
            _ => $"{v:0.###}",
        };
    }
}

// ===========================================================================
// Aufklappbare Überschrift. Kann verschachtelt werden ("Alle Werte" -> Bauteile).
// ===========================================================================
sealed class CollapsibleHeader : Control
{
    static readonly Font HeadFont = new("Segoe UI Semibold", 12f);
    static readonly Font BigFont = new("Segoe UI Semibold", 13f);
    static readonly Font CountFont = new("Segoe UI", 9f);

    readonly Color _accent;
    readonly string _key;
    readonly List<Control> _content = new();
    bool _expanded, _hover, _parentVisible = true;

    public string Count { get; set; }
    public bool Big { get; set; }

    public CollapsibleHeader(string text, string count, Color accent, string key, bool defaultExpanded)
    {
        Text = text;
        Count = count;
        _accent = accent;
        _key = key;
        _expanded = UiState.Instance.IsExpanded(key, defaultExpanded);
        Cursor = Cursors.Hand;
        BackColor = Theme.Background;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public bool Expanded => _expanded;

    public void AddContent(Control c)
    {
        _content.Add(c);
        c.Visible = _parentVisible && _expanded;
    }

    /// Sichtbarkeit von oben nach unten weitergeben
    public void ApplyVisibility(bool parentVisible)
    {
        _parentVisible = parentVisible;
        Visible = parentVisible;
        bool show = parentVisible && _expanded;
        foreach (var c in _content)
        {
            if (c is CollapsibleHeader h) h.ApplyVisibility(show);
            else c.Visible = show;
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _expanded = !_expanded;
        UiState.Instance.Set(_key, _expanded);
        Parent?.SuspendLayout();
        ApplyVisibility(_parentVisible);
        Parent?.ResumeLayout();
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (Big)
        {
            g.Clear(Theme.Background);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Gfx.RoundRect(r, (int)(10 * k));
            using var bg = new SolidBrush(_hover ? Theme.CardHero : Theme.Card);
            g.FillPath(bg, path);
            using var bp = new Pen(Theme.Border);
            g.DrawPath(bp, path);
        }
        else
        {
            g.Clear(_hover ? Theme.Header : Theme.Background);
            using var b = new SolidBrush(_accent);
            g.FillRectangle(b, 0, (int)(Height * 0.28f), (int)(4 * k), (int)(Height * 0.5f));
        }

        // Pfeil ▶ / ▼
        float cx = 22 * k, cy = Height / 2f, s = 5 * k;
        var tri = _expanded
            ? new[] { new PointF(cx - s, cy - s / 2), new PointF(cx + s, cy - s / 2), new PointF(cx, cy + s * 0.8f) }
            : new[] { new PointF(cx - s / 2, cy - s), new PointF(cx - s / 2, cy + s), new PointF(cx + s * 0.8f, cy) };
        using (var b = new SolidBrush(Big ? _accent : Theme.Muted)) g.FillPolygon(b, tri);

        int x = (int)(36 * k);
        TextRenderer.DrawText(g, Text, Big ? BigFont : HeadFont, new Rectangle(x, 0, Width - x - (int)(240 * k), Height), Theme.Text,
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Count, CountFont,
            new Rectangle(Width - (int)(250 * k), 0, (int)(236 * k), Height), Theme.Muted,
            TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        if (!Big)
        {
            using var line = new Pen(Theme.Border);
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }
}

// ===========================================================================
// Tabelle aller Werte eines Bauteils (wie HWMonitor): Name · Wert · Min · Max
// Gruppen (Spannungen, Takt …) lassen sich einzeln auf- und zuklappen.
// Doppelklick auf eine Zeile setzt Min/Max zurück.
// ===========================================================================
sealed class SensorTable : Control
{
    static readonly Font RowFont = new("Segoe UI", 9f);
    static readonly Font GroupFont = new("Segoe UI Semibold", 9.5f);
    static readonly Font HeadFont = new("Segoe UI Semibold", 8.5f);

    sealed class Group
    {
        public required string Key;
        public required string Label;
        public required SensorType Type;
        public required List<ISensor> Sensors;
        public bool Expanded;
    }

    readonly List<Group> _groups = new();

    public Func<ISensor, bool>? IsHidden { get; set; }
    public event Action<ISensor>? HideToggled;
    public event Action<ISensor>? Pinned;
    readonly Color _accent;
    readonly string _hwName;
    int _hoverRow = -1;

    float K => DeviceDpi / 96f;
    int RowH => (int)(24 * K);

    public SensorTable(IHardware hw, IEnumerable<ISensor> sensors, Color accent)
    {
        _accent = accent;
        _hwName = hw.Name;
        string hwKey = hw.Identifier.ToString();

        foreach (var grp in sensors.GroupBy(s => s.SensorType).OrderBy(g => SensorFormat.Rank(g.Key)))
        {
            string key = $"{hwKey}|{grp.Key}";
            // Temperaturen, Spannungen, Lüfter, Leistung, Takt und Auslastung standardmäßig offen
            bool def = grp.Key.ToString() is "Temperature" or "Voltage" or "Fan" or "Power" or "Clock" or "Load";
            _groups.Add(new Group
            {
                Key = key,
                Label = SensorFormat.Label(grp.Key),
                Type = grp.Key,
                Sensors = grp.OrderBy(s => s.Index).ToList(),
                Expanded = UiState.Instance.IsExpanded(key, def),
            });
        }

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        new ToolTip().SetToolTip(this, "Kopfzeile: alle auf-/zuklappen · Gruppe anklicken: auf-/zuklappen · Rechtsklick auf einen Wert: ausblenden, anheften, Min/Max zurücksetzen");
    }

    public int SensorCount => _groups.Sum(g => g.Sensors.Count);

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Margin = new Padding((int)(6 * K), (int)(4 * K), (int)(6 * K), (int)(10 * K));
        UpdateHeight();
    }

    // Zeilen: -1 = Spaltenkopf, dann pro Gruppe eine Gruppenzeile + (wenn offen) Sensorzeilen
    IEnumerable<(Group G, ISensor? S)> Rows()
    {
        foreach (var g in _groups)
        {
            yield return (g, null);
            if (g.Expanded)
                foreach (var s in g.Sensors) yield return (g, s);
        }
    }

    void UpdateHeight()
    {
        Height = (Rows().Count() + 1) * RowH + (int)(8 * K);
    }

    (Group G, ISensor? S)? RowAt(int y)
    {
        int idx = y / RowH - 1;
        if (idx < 0) return null;
        int i = 0;
        foreach (var r in Rows())
            if (i++ == idx) return r;
        return null;
    }

    bool AnyExpanded => _groups.Any(g => g.Expanded);

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && e.Y < RowH)
        {
            bool expand = !AnyExpanded;
            foreach (var g in _groups)
            {
                g.Expanded = expand;
                UiState.Instance.Expanded[g.Key] = expand;
            }
            UiState.Instance.Save();
            Parent?.SuspendLayout();
            UpdateHeight();
            Parent?.ResumeLayout();
            Invalidate();
            return;
        }
        if (RowAt(e.Y) is { S: null } row)
        {
            row.G.Expanded = !row.G.Expanded;
            UiState.Instance.Set(row.G.Key, row.G.Expanded);
            Parent?.SuspendLayout();
            UpdateHeight();
            Parent?.ResumeLayout();
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right || RowAt(e.Y) is not { S: not null } row) return;
        var sensor = row.S!;
        bool hidden = IsHidden?.Invoke(sensor) ?? false;
        var m = new ContextMenuStrip();
        m.Items.Add(hidden ? "Einblenden" : "Ausblenden", null, (_, _) => BeginInvoke(() => HideToggled?.Invoke(sensor)));
        m.Items.Add("In der Übersicht anheften", null, (_, _) => BeginInvoke(() => Pinned?.Invoke(sensor)));
        m.Items.Add("Min/Max zurücksetzen", null, (_, _) => { sensor.ResetMin(); sensor.ResetMax(); Invalidate(); });
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(this, e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (RowAt(e.Y) is { S: not null } row)
        {
            row.S!.ResetMin();
            row.S!.ResetMax();
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int r = e.Y / RowH;
        if (r != _hoverRow) { _hoverRow = r; Invalidate(); }
        Cursor = e.Y < RowH || RowAt(e.Y) is { S: null } ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverRow = -1; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = K;
        int rh = RowH;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(rect, (int)(10 * k)))
        using (var bg = new SolidBrush(Theme.Card))
        using (var border = new Pen(Theme.Border))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        int colW = (int)(120 * k);
        int pad = (int)(14 * k);
        int xMax = Width - pad - colW;
        int xMin = xMax - colW;
        int xVal = xMin - colW;
        int nameW = xVal - pad * 2;

        const TextFormatFlags L = TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        const TextFormatFlags R = TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        // Spaltenkopf
        {
            bool open = AnyExpanded;
            float cx = pad + 4 * k, cy = rh / 2f, sz = 4.5f * k;
            var tri = open
                ? new[] { new PointF(cx - sz, cy - sz / 2), new PointF(cx + sz, cy - sz / 2), new PointF(cx, cy + sz * 0.8f) }
                : new[] { new PointF(cx - sz / 2, cy - sz), new PointF(cx - sz / 2, cy + sz), new PointF(cx + sz * 0.8f, cy) };
            using (var b = new SolidBrush(_hoverRow == 0 ? Theme.Text : Theme.Muted)) g.FillPolygon(b, tri);
            TextRenderer.DrawText(g, open ? "Sensor  ·  alle zuklappen" : "Sensor  ·  alle aufklappen", HeadFont,
                new Rectangle(pad + (int)(16 * k), 0, nameW, rh), _hoverRow == 0 ? Theme.Text : Theme.Muted, L);
        }
        TextRenderer.DrawText(g, "Wert", HeadFont, new Rectangle(xVal, 0, colW, rh), Theme.Muted, R);
        TextRenderer.DrawText(g, "Min", HeadFont, new Rectangle(xMin, 0, colW, rh), Theme.Muted, R);
        TextRenderer.DrawText(g, "Max", HeadFont, new Rectangle(xMax, 0, colW, rh), Theme.Muted, R);
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, pad, rh - 1, Width - pad, rh - 1);

        int y = rh;
        int rowIndex = 1;
        foreach (var (grp, s) in Rows())
        {
            var r = new Rectangle(0, y, Width, rh);
            if (rowIndex == _hoverRow)
                using (var hb = new SolidBrush(Theme.CardHero)) g.FillRectangle(hb, 1, y, Width - 2, rh);

            if (s == null)
            {
                // Gruppenzeile mit Pfeil
                float cx = pad + 4 * k, cy = y + rh / 2f, sz = 4 * k;
                var tri = grp.Expanded
                    ? new[] { new PointF(cx - sz, cy - sz / 2), new PointF(cx + sz, cy - sz / 2), new PointF(cx, cy + sz * 0.8f) }
                    : new[] { new PointF(cx - sz / 2, cy - sz), new PointF(cx - sz / 2, cy + sz), new PointF(cx + sz * 0.8f, cy) };
                using (var b = new SolidBrush(_accent)) g.FillPolygon(b, tri);
                TextRenderer.DrawText(g, $"{grp.Label}  ({grp.Sensors.Count})", GroupFont,
                    new Rectangle(pad + (int)(16 * k), y, nameW, rh), Theme.Text, L);
            }
            else
            {
                string name = s.SensorType is SensorType.Fan or SensorType.Control ? FanNaming.Label(s) : s.Name;
                if (s.Hardware.Name != _hwName) name += $"   · {s.Hardware.Name}";
                bool hid = IsHidden?.Invoke(s) ?? false;
                if (hid) name += "   (ausgeblendet)";
                var valCol = s.SensorType == SensorType.Temperature && s.Value is float tv ? Theme.ForTemp(tv) : Theme.Text;
                TextRenderer.DrawText(g, name, RowFont, new Rectangle(pad + (int)(16 * k), y, nameW - (int)(16 * k), rh), hid ? Theme.Muted : Theme.Text, L);
                TextRenderer.DrawText(g, SensorFormat.Value(s.SensorType, s.Value), RowFont, new Rectangle(xVal, y, colW, rh), valCol, R);
                TextRenderer.DrawText(g, SensorFormat.Value(s.SensorType, s.Min), RowFont, new Rectangle(xMin, y, colW, rh), Theme.Muted, R);
                TextRenderer.DrawText(g, SensorFormat.Value(s.SensorType, s.Max), RowFont, new Rectangle(xMax, y, colW, rh), Theme.Muted, R);
            }
            y += rh;
            rowIndex++;
        }
    }
}

// ===========================================================================
// Treiber PawnIO: nötig für CPU- und Mainboard-Sensoren sowie Mainboard-Lüfter
// ===========================================================================
static class Driver
{
    const string SetupUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    public static bool PawnIoInstalled
    {
        get
        {
            // Gleiche Prüfung wie LibreHardwareMonitor: Eintrag unter "Programme und Features"
            const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
            try
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = hklm.OpenSubKey(sub);
                    if (key?.GetValue("DisplayVersion") is string v && Version.TryParse(v, out _)) return true;
                }
                using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                return svc != null;
            }
            catch { return true; } // im Zweifel keinen Hinweis zeigen
        }
    }

    /// Lädt den offiziellen PawnIO-Installer herunter und startet ihn. true = danach installiert.
    public static async Task<bool> InstallAsync(IWin32Window owner)
    {
        string path = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
        using (var progress = new ProgressForm("Lade den Treiber PawnIO herunter …"))
        {
            progress.Show(owner);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TempMonitor", "1.0"));
                var bytes = await http.GetByteArrayAsync(SetupUrl);
                await File.WriteAllBytesAsync(path, bytes);
            }
            catch (Exception ex)
            {
                progress.Close();
                MessageBox.Show(owner, "Download fehlgeschlagen:\n" + ex.Message +
                    "\n\nDu kannst PawnIO auch selbst von https://pawnio.eu installieren.",
                    "PawnIO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            progress.Close();
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo(path, "-install") { UseShellExecute = true });
            if (p != null) await p.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Installer konnte nicht gestartet werden:\n" + ex.Message, "PawnIO",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return PawnIoInstalled;
    }
}

// ===========================================================================
// Gelber Hinweis-Balken, wenn der Treiber fehlt
// ===========================================================================
sealed class DriverBanner : Panel
{
    public event Func<Task>? InstallClicked;

    public DriverBanner()
    {
        float k = DeviceDpi / 96f;
        Height = (int)(76 * k);
        BackColor = Color.FromArgb(58, 46, 18);
        Margin = new Padding((int)(6 * k), (int)(10 * k), (int)(6 * k), (int)(4 * k));
        Padding = new Padding((int)(16 * k), (int)(10 * k), (int)(16 * k), (int)(10 * k));

        var btn = new Button
        {
            Text = "Treiber installieren",
            Dock = DockStyle.Right,
            Width = (int)(180 * k),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Ok,
            ForeColor = Color.FromArgb(30, 24, 6),
            Font = new Font("Segoe UI Semibold", 10f),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += async (_, _) =>
        {
            btn.Enabled = false;
            try { if (InstallClicked != null) await InstallClicked(); }
            finally { btn.Enabled = true; }
        };

        var text = new Label
        {
            Text = "⚠  CPU- und Mainboard-Werte fehlen: Der Treiber „PawnIO“ ist nicht installiert.\n" +
                   "Er wird gebraucht, um Temperaturen, Spannungen und Lüfter direkt vom Prozessor und Mainboard zu lesen.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(253, 230, 138),
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        Controls.Add(text);
        Controls.Add(btn);
    }
}
