using System.Drawing.Drawing2D;
using System.Drawing.Text;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

// ===========================================================================
// Verlauf aller Sensoren (5 Minuten). Bleibt erhalten, wenn Ansichten neu aufgebaut werden.
// ===========================================================================
sealed class HistoryStore
{
    public const int MaxPoints = 300;
    readonly object _lock = new();
    readonly Dictionary<string, List<float>> _data = new();
    readonly Dictionary<string, long> _total = new();

    /// Wird jede Sekunde im Hintergrund aufgerufen
    public void Record(IComputer computer)
    {
        lock (_lock)
        {
            foreach (var hw in computer.Hardware)
                foreach (var s in MainForm.AllSensors(hw))
                {
                    if (s.Value is not float v || float.IsNaN(v)) continue;
                    string id = s.Identifier.ToString();
                    if (!_data.TryGetValue(id, out var list)) { list = new List<float>(MaxPoints + 1); _data[id] = list; }
                    list.Add(v);
                    if (list.Count > MaxPoints) list.RemoveAt(0);
                    _total[id] = (_total.TryGetValue(id, out var n) ? n : 0) + 1;
                }
        }
    }

    /// Werte + Anzahl aller bisherigen Messungen (damit Abschnitte fest an der Zeit hängen)
    public (float[] Data, long Total) GetWithTotal(ISensor? s)
    {
        if (s == null) return (Array.Empty<float>(), 0);
        lock (_lock)
        {
            string id = s.Identifier.ToString();
            return _data.TryGetValue(id, out var l)
                ? (l.ToArray(), _total.TryGetValue(id, out var n) ? n : l.Count)
                : (Array.Empty<float>(), 0);
        }
    }

    public float[] Get(ISensor? s)
    {
        if (s == null) return Array.Empty<float>();
        lock (_lock)
            return _data.TryGetValue(s.Identifier.ToString(), out var l) ? l.ToArray() : Array.Empty<float>();
    }
}

// ===========================================================================
// Kacheln der Übersicht (gespeichert in ui.json)
// ===========================================================================
enum TileSize { Small, Medium, Large }

sealed class TileConfig
{
    public string Kind { get; set; } = "hw";   // "hw" = ganzes Bauteil, "sensor" = einzelner Sensor
    public string Id { get; set; } = "";
    public TileSize Size { get; set; } = TileSize.Medium;
}

static class TileSizes
{
    public static string Name(TileSize s) => s switch { TileSize.Small => "Klein", TileSize.Large => "Groß", _ => "Mittel" };
}

// ===========================================================================
// Hilfen rund um Hardware & Sensoren
// ===========================================================================
static class Hw
{
    public static IHardware? Find(IComputer c, string id) =>
        c.Hardware.FirstOrDefault(h => h.Identifier.ToString() == id);

    public static (ISensor Sensor, IHardware Top)? FindSensor(IComputer c, string id)
    {
        foreach (var hw in c.Hardware)
            foreach (var s in MainForm.AllSensors(hw))
                if (s.Identifier.ToString() == id) return (s, hw);
        return null;
    }

    public static string Category(IHardware hw) => MainForm.Category(hw.HardwareType);

    /// Wichtigster Wert eines Bauteils: Haupttemperatur, sonst Auslastung, sonst erster Wert
    public static ISensor? Main(IHardware hw)
    {
        var temps = MainForm.AllSensors(hw).Where(MainForm.IsUsefulTemp).ToList();
        if (temps.Count > 0) return MainForm.PickMain(hw, temps);
        return MainForm.MainLoad(hw) ?? MainForm.AllSensors(hw).FirstOrDefault(s => s.Value.HasValue);
    }

    /// Passende Auslastung zu einem Wert: gleicher Name (z. B. "CPU Core #3") oder Gesamtlast
    public static ISensor? LoadFor(IComputer c, IHardware hw, ISensor s)
    {
        if (s.SensorType == SensorType.Load) return null;
        var same = MainForm.AllSensors(hw).FirstOrDefault(x => x.SensorType == SensorType.Load && x.Name == s.Name);
        if (same != null) return same;
        if (hw.HardwareType == HardwareType.Motherboard)
            return c.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).Select(MainForm.MainLoad).FirstOrDefault();
        return MainForm.MainLoad(hw);
    }

    public static string SensorLabel(ISensor s) =>
        s.SensorType is SensorType.Fan or SensorType.Control ? FanNaming.Label(s) : s.Name;

    public static bool IsEmptyFanPort(ISensor s) => FanNaming.Hidden.Contains(s.Identifier.ToString());

    /// Standard-Übersicht: CPU, GPU, Mainboard mittel, Rest klein
    public static List<TileConfig> DefaultTiles(IComputer c)
    {
        var list = new List<TileConfig>();
        foreach (var hw in c.Hardware.OrderBy(h => MainForm.Order(h.HardwareType)))
        {
            if (!MainForm.AllSensors(hw).Any(MainForm.IsUsefulTemp)) continue;
            bool big = hw.HardwareType is HardwareType.Cpu or HardwareType.Motherboard
                or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
            list.Add(new TileConfig { Kind = "hw", Id = hw.Identifier.ToString(), Size = big ? TileSize.Medium : TileSize.Small });
        }
        return list;
    }
}

// ===========================================================================
// Eine Kachel: Bauteil oder einzelner Sensor, in drei Größen
// ===========================================================================
sealed class SensorTile : Control
{
    static readonly Font TitleS = new("Segoe UI Semibold", 10f);
    static readonly Font TitleM = new("Segoe UI Semibold", 12f);
    static readonly Font SubFont = new("Segoe UI", 8.5f);
    static readonly Font ValueS = new("Segoe UI Semibold", 20f);
    static readonly Font ValueM = new("Segoe UI Semibold", 28f);
    static readonly Font BadgeFont = new("Segoe UI Semibold", 8.5f);
    static readonly Font TinyFont = new("Segoe UI", 7.5f);

    readonly HistoryStore _hist;
    readonly string _title, _subtitle;
    readonly Color _accent;
    readonly bool _component;
    bool _hover;

    public ISensor Sensor { get; }
    public ISensor? Load { get; }
    public TileSize TileSize { get; }
    public bool Dimmed { get; set; }
    public bool Clickable { get; set; }
    public event Action? Opened;

    public SensorTile(HistoryStore hist, ISensor sensor, ISensor? load, string title, string subtitle,
                      Color accent, TileSize size, bool component)
    {
        _hist = hist;
        Sensor = sensor;
        Load = load;
        _title = title;
        _subtitle = subtitle;
        _accent = accent;
        TileSize = size;
        _component = component;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
    }

    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    protected override void OnCreateControl() { base.OnCreateControl(); ApplySize(); }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); ApplySize(); }

    void ApplySize()
    {
        Size = TileSize switch
        {
            TileSize.Small => new Size(S(196), S(112)),
            TileSize.Large => new Size(S(596), S(240)),
            _ => new Size(S(292), S(240)),
        };
        Margin = new Padding(S(6));
        Cursor = Clickable ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && Clickable) Opened?.Invoke();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!Clickable) { Sensor.ResetMin(); Sensor.ResetMax(); Invalidate(); }
    }

    bool IsTemp => Sensor.SensorType == SensorType.Temperature;

    string ValueText(float v, bool compact) => IsTemp
        ? (compact ? $"{v:0}°" : $"{v:0}°")
        : SensorFormat.Value(Sensor.SensorType, v);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = K;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        // Hintergrund
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(rect, S(12)))
        {
            using var bg = new SolidBrush(_hover && Clickable ? Theme.CardHero : Theme.Card);
            g.FillPath(bg, path);
            var oldClip = g.Clip;
            g.SetClip(path);
            using (var acc = new SolidBrush(_accent)) g.FillRectangle(acc, 0, 0, Width, S(3));
            g.Clip = oldClip;
            using var border = new Pen(_hover && Clickable ? Color.FromArgb(120, _accent) : Theme.Border);
            g.DrawPath(border, path);
        }

        const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        float? cur = Display();
        var valCol = cur is float c && IsTemp ? Theme.ForTemp(c) : Theme.Text;
        var titleCol = _component ? _accent : Theme.Text;
        float? load = Load?.Value;

        if (TileSize == TileSize.Small)
        {
            TextRenderer.DrawText(g, _title, TitleS, new Rectangle(S(14), S(10), Width - S(28), S(20)), titleCol, L);
            TextRenderer.DrawText(g, _subtitle, SubFont, new Rectangle(S(14), S(30), Width - S(28), S(16)), Theme.Muted, L);
            TextRenderer.DrawText(g, cur is float v ? ValueText(v, true) : "–", ValueS,
                new Rectangle(S(10), S(52), Width - S(20), S(44)), valCol, L);
            if (load is float ld)
                TextRenderer.DrawText(g, $"Last {ld:0} %", BadgeFont, new Rectangle(S(90), S(68), Width - S(104), S(18)), Theme.Accent, R);
        }
        else
        {
            TextRenderer.DrawText(g, _title, TitleM, new Rectangle(S(16), S(12), Width - S(32), S(24)), titleCol, L);
            TextRenderer.DrawText(g, _subtitle, SubFont, new Rectangle(S(16), S(36), Width - S(32), S(16)), Theme.Muted, L);
            TextRenderer.DrawText(g, cur is float v ? ValueText(v, false) : "–", ValueM,
                new Rectangle(S(12), S(54), S(220), S(50)), valCol, L);

            // rechte Spalte: Status, Last, Min/Max
            if (cur is float vv && IsTemp) DrawBadge(g, Theme.Status(vv), Theme.ForTemp(vv), new Point(Width - S(16), S(58)), k);
            if (load is float ld)
                TextRenderer.DrawText(g, $"Last {ld:0} %", BadgeFont, new Rectangle(Width - S(150), S(84), S(134), S(18)), Theme.Accent, R);
            if (Sensor.Min is float mn && Sensor.Max is float mx)
            {
                string mm = IsTemp ? $"min {mn:0}°  ·  max {mx:0}°"
                    : $"min {SensorFormat.Value(Sensor.SensorType, mn)}  ·  max {SensorFormat.Value(Sensor.SensorType, mx)}";
                TextRenderer.DrawText(g, mm, TinyFont, new Rectangle(S(16), S(104), Width - S(32), S(16)), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            DrawGraph(g, new Rectangle(S(16), S(124), Width - S(32), Height - S(136)), IsTemp && cur is float t ? Theme.ForTemp(t) : _accent, k);
        }

        if (Dimmed)
        {
            using var dim = new SolidBrush(Color.FromArgb(150, Theme.Background));
            g.FillRectangle(dim, 0, 0, Width, Height);
            TextRenderer.DrawText(g, "ausgeblendet", BadgeFont, new Rectangle(0, 0, Width, Height), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    void DrawBadge(Graphics g, string text, Color col, Point rightTop, float k)
    {
        var sz = TextRenderer.MeasureText(text, BadgeFont);
        var r = new Rectangle(rightTop.X - sz.Width - (int)(16 * k), rightTop.Y, sz.Width + (int)(16 * k), (int)(22 * k));
        using (var p = Gfx.RoundRect(r, r.Height / 2))
        using (var b = new SolidBrush(Color.FromArgb(40, col)))
            g.FillPath(b, p);
        TextRenderer.DrawText(g, text, BadgeFont, r, col,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// Gleitender Mittelwert: nimmt das Sekunden-Rauschen der Sensoren raus
    static float[] Smooth(float[] d, int window)
    {
        if (d.Length == 0 || window <= 1) return d;
        var r = new float[d.Length];
        double sum = 0;
        for (int i = 0; i < d.Length; i++)
        {
            sum += d[i];
            if (i >= window) sum -= d[i - window];
            r[i] = (float)(sum / Math.Min(i + 1, window));
        }
        return r;
    }

    /// Verdichten in Abschnitte, die fest an der Zeit hängen: Abschnitt = (Messung Nr.) / step.
    /// So bleibt die Kurve stabil und nur das Stück ganz rechts bewegt sich.
    static (float[] Values, float[] Pos) Bucket(float[] d, long total, int step)
    {
        if (step <= 1) return (d, Enumerable.Range(0, d.Length).Select(i => (float)i).ToArray());
        var vals = new List<float>();
        var pos = new List<float>();
        long first = total - d.Length;
        long cur = long.MinValue;
        float sum = 0, psum = 0;
        int n = 0;
        for (int i = 0; i < d.Length; i++)
        {
            long b = (first + i) / step;
            if (b != cur && n > 0)
            {
                vals.Add(sum / n);
                pos.Add(psum / n);
                sum = psum = 0;
                n = 0;
            }
            cur = b;
            sum += d[i];
            psum += i;
            n++;
        }
        if (n > 0)
        {
            vals.Add(sum / n);
            pos.Add(d.Length - 1);   // letzter Punkt immer ganz rechts ("jetzt")
        }
        return (vals.ToArray(), pos.ToArray());
    }

    float? _lo, _hi;   // Skala mit Trägheit

    /// Wert für die große Zahl: Mittel der letzten 3 Sekunden (flackert nicht)
    float? Display()
    {
        var d = _hist.Get(Sensor);
        if (d.Length == 0) return Sensor.Value;
        int n = Math.Min(3, d.Length);
        float sum = 0;
        for (int i = d.Length - n; i < d.Length; i++) sum += d[i];
        return sum / n;
    }

    void DrawGraph(Graphics g, Rectangle gr, Color col, float k)
    {
        var (raw, total) = _hist.GetWithTotal(Sensor);
        if (raw.Length < 2 || gr.Width < 20 || gr.Height < 20) return;

        int step = Math.Max(1, (int)Math.Ceiling(HistoryStore.MaxPoints / (gr.Width / Math.Max(2f, 3 * k))));
        var (data, pos) = Bucket(Smooth(raw, 5), total, step);

        // ----- Skala: in 10er-Stufen, ändert sich nur, wenn Werte herauslaufen oder viel Platz frei ist -----
        float lo, hi;
        string type = Sensor.SensorType.ToString();
        if (type is "Load" or "Control" or "Level" or "Humidity") { lo = 0; hi = 100; }
        else
        {
            float dMin = data.Min(), dMax = data.Max();
            float needLo, needHi;
            if (IsTemp)
            {
                needLo = (float)Math.Floor((dMin - 3) / 10f) * 10f;
                needHi = (float)Math.Ceiling((dMax + 3) / 10f) * 10f;
                if (needLo < 0 && dMin >= 0) needLo = 0;
                if (needHi - needLo < 20) needHi = needLo + 20;
            }
            else
            {
                float pad = Math.Max((dMax - dMin) * 0.2f, Math.Abs(dMax) * 0.03f + 0.001f);
                needLo = dMin - pad; needHi = dMax + pad;
                if (needLo < 0 && dMin >= 0) needLo = 0;
            }
            bool outside = _lo is null || dMin < _lo || dMax > _hi;
            bool tooLoose = _lo is float l0 && _hi is float h0 && (h0 - l0) > (needHi - needLo) * 1.8f;
            if (outside || tooLoose) { _lo = needLo; _hi = needHi; }
            lo = _lo!.Value; hi = _hi!.Value;
        }

        // ----- Hilfslinien -----
        using (var gridPen = new Pen(Color.FromArgb(70, Theme.Grid)) { DashStyle = DashStyle.Dot })
        {
            g.DrawLine(gridPen, gr.Left, gr.Top, gr.Right, gr.Top);
            g.DrawLine(gridPen, gr.Left, gr.Top + gr.Height / 2f, gr.Right, gr.Top + gr.Height / 2f);
        }
        using (var basePen = new Pen(Theme.Grid))
            g.DrawLine(basePen, gr.Left, gr.Bottom, gr.Right, gr.Bottom);

        float dx = gr.Width / (float)(HistoryStore.MaxPoints - 1);

        // ----- Auslastung: ruhige Fläche im Hintergrund (0–100 % der Höhe) -----
        var (loadRaw, loadTotal) = _hist.GetWithTotal(Load);
        if (loadRaw.Length >= 2)
        {
            var (ld, lpos) = Bucket(Smooth(loadRaw, 8), loadTotal, step);
            var lp = new List<PointF>(ld.Length + 2);
            for (int i = 0; i < ld.Length; i++)
                lp.Add(new PointF(gr.Right - (loadRaw.Length - 1 - lpos[i]) * dx,
                                  gr.Bottom - Math.Clamp(ld[i], 0, 100) / 100f * gr.Height));
            if (lp.Count >= 2)
            {
                using var path = new GraphicsPath();
                path.AddLines(lp.ToArray());
                path.AddLine(lp[^1].X, lp[^1].Y, lp[^1].X, gr.Bottom);
                path.AddLine(lp[^1].X, gr.Bottom, lp[0].X, gr.Bottom);
                path.CloseFigure();
                using (var lf = new SolidBrush(Color.FromArgb(34, Theme.Accent))) g.FillPath(lf, path);
                using var edge = new Pen(Color.FromArgb(80, Theme.Accent), 1f * k) { LineJoin = LineJoin.Round };
                g.DrawLines(edge, lp.ToArray());
            }
        }

        // ----- Wert als Linie davor -----
        var pts = new PointF[data.Length];
        for (int i = 0; i < data.Length; i++)
            pts[i] = new PointF(gr.Right - (raw.Length - 1 - pos[i]) * dx,
                                gr.Bottom - Math.Clamp((data[i] - lo) / (hi - lo), 0, 1) * gr.Height);

        if (pts.Length >= 2)
        {
            using var line = new GraphicsPath();
            line.AddCurve(pts, 0.35f);
            using (var fillPath = (GraphicsPath)line.Clone())
            {
                fillPath.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, gr.Bottom);
                fillPath.AddLine(pts[^1].X, gr.Bottom, pts[0].X, gr.Bottom);
                fillPath.CloseFigure();
                using var fill = new LinearGradientBrush(new Rectangle(gr.X, gr.Y - 1, gr.Width, gr.Height + 2),
                    Color.FromArgb(55, col), Color.FromArgb(0, col), LinearGradientMode.Vertical);
                g.FillPath(fill, fillPath);
            }
            using var pen = new Pen(col, 2f * k) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(pen, line);
        }
        var p = pts[^1];
        using (var dot = new SolidBrush(col))
            g.FillEllipse(dot, p.X - 3.5f * k, p.Y - 3.5f * k, 7f * k, 7f * k);

        // ----- Skala-Werte, klein in der Ecke -----
        if (IsTemp)
        {
            var f = TextFormatFlags.Left | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, $"{hi:0}°", TinyFont, new Point(gr.Left + 2, gr.Top + 2), Theme.Muted, f);
            TextRenderer.DrawText(g, $"{lo:0}°", TinyFont, new Point(gr.Left + 2, gr.Bottom - (int)(14 * k)), Theme.Muted, f);
        }
    }
}

// ===========================================================================
// Kopfzeile einer Seite: Titel links, Knöpfe rechts
// ===========================================================================
sealed class PageBar : Panel
{
    public PageBar(string title, string hint, float k)
    {
        Height = (int)(56 * k);
        BackColor = Theme.Background;
        Padding = new Padding((int)(6 * k), (int)(10 * k), (int)(6 * k), (int)(6 * k));
        var t = new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Theme.Text,
            AutoSize = true,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, (int)(4 * k), (int)(12 * k), 0),
        };
        var h = new Label
        {
            Text = hint,
            ForeColor = Theme.Muted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        Controls.Add(h);
        Controls.Add(t);
    }

    public void AddRight(Control c)
    {
        c.Dock = DockStyle.Right;
        Controls.Add(c);
        c.BringToFront();
        foreach (Control x in Controls) if (x.Dock == DockStyle.Fill) x.BringToFront();
    }

    public static Button Button(string text, float k, int width = 180)
    {
        var b = new Button
        {
            Text = text,
            Width = (int)(width * k),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.CardHero;
        return b;
    }
}

// ===========================================================================
// Übersicht: frei zusammenstellbare Kacheln
// ===========================================================================
sealed class DashboardView : FlowLayoutPanel
{
    readonly IComputer _computer;
    readonly HistoryStore _hist;
    readonly Action<IHardware> _open;
    readonly Func<Control?> _banner;
    readonly List<SensorTile> _tiles = new();
    readonly List<SensorTable> _tables = new();
    readonly List<Control> _full = new();

    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public DashboardView(IComputer computer, HistoryStore hist, Action<IHardware> open, Func<Control?> banner)
    {
        _computer = computer;
        _hist = hist;
        _open = open;
        _banner = banner;
        AutoScroll = true;
        BackColor = Theme.Background;
        Padding = new Padding(S(12), S(6), S(12), S(12));
        Resize += (_, _) => ResizeFull();

        var ui = UiState.Instance;
        if (!ui.TilesInitialized)
        {
            ui.Tiles = Hw.DefaultTiles(computer);
            ui.TilesInitialized = true;
            ui.Save();
        }
        Rebuild();
    }

    void AddFull(Control c)
    {
        _full.Add(c);
        Controls.Add(c);
        SetFlowBreak(c, true);
    }

    void ResizeFull()
    {
        int w = Math.Max(100, ClientSize.Width - Padding.Horizontal - S(16));
        foreach (var c in _full) c.Width = w;
    }

    public void Rebuild()
    {
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var c in old) c.Dispose();
        _tiles.Clear();
        _tables.Clear();
        _full.Clear();

        if (_banner() is Control b) AddFull(b);

        var bar = new PageBar("Übersicht", "Kachel anklicken öffnet die Detailseite  ·  Rechtsklick: Größe, Position, entfernen", K)
        {
            Margin = new Padding(S(6), S(4), S(6), S(2)),
        };
        var customize = PageBar.Button("Übersicht anpassen …", K);
        customize.Click += (_, _) => Customize();
        bar.AddRight(customize);
        AddFull(bar);

        var tiles = UiState.Instance.Tiles;
        foreach (var cfg in tiles.ToList())
        {
            var tile = CreateTile(cfg);
            if (tile == null) continue;
            tile.ContextMenuStrip = TileMenu(cfg, tile);
            _tiles.Add(tile);
            Controls.Add(tile);
        }

        if (_tiles.Count == 0)
        {
            var hint = new Label
            {
                Text = "Die Übersicht ist leer. Über „Übersicht anpassen …“ legst du fest, was hier angezeigt wird.",
                ForeColor = Theme.Muted,
                AutoSize = true,
                Font = new Font("Segoe UI", 10.5f),
                Margin = new Padding(S(10), S(16), 0, 0),
            };
            Controls.Add(hint);
            SetFlowBreak(hint, true);
        }
        else SetFlowBreak(_tiles[^1], true);

        // ----- Sensor-Bereiche der ausgewählten Bauteile -----
        var sections = UiState.Instance.DashboardSections;
        foreach (var hw in _computer.Hardware.OrderBy(h => MainForm.Order(h.HardwareType))
                                             .Where(h => sections.Contains(h.Identifier.ToString())))
            AddSensorSection(hw);

        ResizeFull();
        ResumeLayout();
    }

    void AddSensorSection(IHardware hw)
    {
        string cat = Hw.Category(hw);
        var accent = Theme.ForCategory(cat);
        string hid = hw.Identifier.ToString();
        var all = MainForm.AllSensors(hw)
            .Where(s => s.Value.HasValue && !Hw.IsEmptyFanPort(s) && !UiState.Instance.IsHidden(s.Identifier.ToString()))
            .ToList();
        if (all.Count == 0) return;

        var header = new CollapsibleHeader($"{cat}  ·  {hw.Name}", $"{all.Count} Werte", accent, "dash|" + hid, defaultExpanded: true)
        {
            Height = S(42),
            Margin = new Padding(S(6), S(14), S(6), S(4)),
        };
        var hm = new ContextMenuStrip();
        hm.Items.Add("Detailseite öffnen", null, (_, _) => BeginInvoke(() => _open(hw)));
        hm.Items.Add("Aus der Übersicht entfernen", null, (_, _) => Change(() => UiState.Instance.DashboardSections.Remove(hid)));
        header.ContextMenuStrip = hm;
        AddFull(header);

        // Temperatur-Graphen
        SensorTile? last = null;
        foreach (var s in all.Where(MainForm.IsUsefulTemp))
        {
            string sub = s.Hardware.Name != hw.Name ? s.Hardware.Name : cat;
            var tile = new SensorTile(_hist, s, Hw.LoadFor(_computer, hw, s), s.Name, sub, accent, TileSize.Medium, component: false);
            var sensor = s;
            var m = new ContextMenuStrip();
            m.Items.Add("Ausblenden", null, (_, _) => Change(() => UiState.Instance.ToggleHidden(sensor.Identifier.ToString())));
            m.Items.Add("Als eigene Kachel anheften", null, (_, _) => BeginInvoke(() => AddSensorTile(sensor)));
            m.Items.Add("Min/Max zurücksetzen", null, (_, _) => { sensor.ResetMin(); sensor.ResetMax(); });
            tile.ContextMenuStrip = m;
            _tiles.Add(tile);
            Controls.Add(tile);
            header.AddContent(tile);
            last = tile;
        }
        if (last != null) SetFlowBreak(last, true);

        // Alle Werte
        var table = new SensorTable(hw, all, accent) { IsHidden = s => UiState.Instance.IsHidden(s.Identifier.ToString()) };
        table.HideToggled += s => Change(() => UiState.Instance.ToggleHidden(s.Identifier.ToString()));
        table.Pinned += s => AddSensorTile(s);
        _tables.Add(table);
        AddFull(table);
        header.AddContent(table);

        header.ApplyVisibility(true);
    }

    SensorTile? CreateTile(TileConfig cfg)
    {
        if (cfg.Kind == "hw")
        {
            var hw = Hw.Find(_computer, cfg.Id);
            if (hw == null || Hw.Main(hw) is not ISensor main) return null;
            string cat = Hw.Category(hw);
            var t = new SensorTile(_hist, main, Hw.LoadFor(_computer, hw, main), cat, hw.Name,
                Theme.ForCategory(cat), cfg.Size, component: true) { Clickable = true };
            t.Opened += () => _open(hw);
            return t;
        }
        else
        {
            var found = Hw.FindSensor(_computer, cfg.Id);
            if (found is null) return null;
            var (s, top) = found.Value;
            string cat = Hw.Category(top);
            var load = s.SensorType == SensorType.Temperature ? Hw.LoadFor(_computer, top, s) : null;
            var t = new SensorTile(_hist, s, load, Hw.SensorLabel(s), $"{cat} · {top.Name}",
                Theme.ForCategory(cat), cfg.Size, component: false) { Clickable = true };
            t.Opened += () => _open(top);
            return t;
        }
    }

    ContextMenuStrip TileMenu(TileConfig cfg, SensorTile tile)
    {
        var m = new ContextMenuStrip();
        var size = new ToolStripMenuItem("Größe");
        foreach (var sz in new[] { TileSize.Small, TileSize.Medium, TileSize.Large })
        {
            var item = new ToolStripMenuItem(TileSizes.Name(sz)) { Checked = cfg.Size == sz };
            item.Click += (_, _) => Change(() => cfg.Size = sz);
            size.DropDownItems.Add(item);
        }
        m.Items.Add(size);
        m.Items.Add("Nach vorne", null, (_, _) => Change(() => Move(cfg, -1)));
        m.Items.Add("Nach hinten", null, (_, _) => Change(() => Move(cfg, +1)));
        m.Items.Add("Aus der Übersicht entfernen", null, (_, _) => Change(() => UiState.Instance.Tiles.Remove(cfg)));
        m.Items.Add(new ToolStripSeparator());
        if (cfg.Kind == "hw")
        {
            var secs = UiState.Instance.DashboardSections;
            var show = new ToolStripMenuItem("Sensoren in der Übersicht anzeigen") { Checked = secs.Contains(cfg.Id) };
            show.Click += (_, _) => Change(() => { if (!secs.Remove(cfg.Id)) secs.Add(cfg.Id); });
            m.Items.Add(show);
        }
        m.Items.Add("Detailseite öffnen", null, (_, _) => tile.BeginInvoke(() =>
        {
            if (cfg.Kind == "hw" && Hw.Find(_computer, cfg.Id) is IHardware h) _open(h);
            else if (Hw.FindSensor(_computer, cfg.Id) is { } f) _open(f.Top);
        }));
        m.Items.Add("Übersicht anpassen …", null, (_, _) => BeginInvoke(Customize));
        return m;
    }

    static void Move(TileConfig cfg, int dir)
    {
        var list = UiState.Instance.Tiles;
        int i = list.IndexOf(cfg), j = i + dir;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
    }

    void Change(Action a)
    {
        a();
        UiState.Instance.Save();
        BeginInvoke(Rebuild);   // erst nach dem Schließen des Menüs neu aufbauen
    }

    void Customize()
    {
        using var dlg = new CustomizeDialog(_computer);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Rebuild();
    }

    public void AddSensorTile(ISensor s)
    {
        var tiles = UiState.Instance.Tiles;
        string id = s.Identifier.ToString();
        if (!tiles.Any(t => t.Kind == "sensor" && t.Id == id))
            tiles.Add(new TileConfig { Kind = "sensor", Id = id, Size = TileSize.Small });
        UiState.Instance.Save();
        Rebuild();
    }

    public void RefreshValues()
    {
        foreach (var t in _tiles) if (t.Visible) t.Invalidate();
        foreach (var t in _tables) if (t.Visible) t.Invalidate();
    }
}

// ===========================================================================
// Detailseite eines Bauteils: nur dessen Graphen und Werte
// ===========================================================================
sealed class ComponentPage : FlowLayoutPanel
{
    readonly IComputer _computer;
    readonly IHardware _hw;
    readonly HistoryStore _hist;
    readonly Action _back;
    readonly Action<ISensor> _pin;
    readonly List<SensorTile> _tiles = new();
    readonly List<Control> _full = new();
    SensorTable? _table;
    bool _showHidden;

    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public IHardware Hardware => _hw;

    public ComponentPage(IComputer computer, IHardware hw, HistoryStore hist, Action back, Action<ISensor> pin)
    {
        _computer = computer;
        _hw = hw;
        _hist = hist;
        _back = back;
        _pin = pin;
        AutoScroll = true;
        BackColor = Theme.Background;
        Padding = new Padding(S(12), S(6), S(12), S(12));
        Resize += (_, _) => ResizeFull();
        Build();
    }

    void AddFull(Control c)
    {
        _full.Add(c);
        Controls.Add(c);
        SetFlowBreak(c, true);
    }

    void ResizeFull()
    {
        int w = Math.Max(100, ClientSize.Width - Padding.Horizontal - S(16));
        foreach (var c in _full) c.Width = w;
    }

    static bool Hidden(ISensor s) => UiState.Instance.IsHidden(s.Identifier.ToString());

    void ToggleHidden(ISensor s)
    {
        UiState.Instance.ToggleHidden(s.Identifier.ToString());
        BeginInvoke(Build);
    }

    void Build()
    {
        SuspendLayout();
        int scroll = VerticalScroll.Value;
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var c in old) c.Dispose();
        _tiles.Clear();
        _full.Clear();

        string cat = Hw.Category(_hw);
        var accent = Theme.ForCategory(cat);
        var all = MainForm.AllSensors(_hw).Where(s => s.Value.HasValue && !Hw.IsEmptyFanPort(s)).ToList();
        int hiddenCount = all.Count(Hidden);

        var bar = new PageBar($"{cat}", _hw.Name, K) { Margin = new Padding(S(6), S(4), S(6), S(2)) };
        var back = PageBar.Button("←  Übersicht", K, 130);
        back.Click += (_, _) => _back();
        var show = new CheckBox
        {
            Text = hiddenCount > 0 ? $"Ausgeblendete anzeigen ({hiddenCount})" : "Ausgeblendete anzeigen",
            Checked = _showHidden,
            ForeColor = Theme.Muted,
            Width = S(230),
            CheckAlign = ContentAlignment.MiddleRight,
            TextAlign = ContentAlignment.MiddleRight,
            Enabled = hiddenCount > 0 || _showHidden,
        };
        show.CheckedChanged += (_, _) => { _showHidden = show.Checked; BeginInvoke(Build); };
        bar.AddRight(show);
        bar.AddRight(new Panel { Width = S(12), BackColor = Theme.Background });
        bar.AddRight(back);
        AddFull(bar);

        // ----- Temperatur-Graphen -----
        var temps = all.Where(MainForm.IsUsefulTemp).Where(s => _showHidden || !Hidden(s)).ToList();
        if (temps.Count > 0)
        {
            AddFull(new SectionHeader("Temperaturen", accent) { Height = S(36), Margin = new Padding(S(6), S(8), S(6), S(2)) });
            SensorTile? last = null;
            foreach (var s in temps)
            {
                string sub = s.Hardware.Name != _hw.Name ? s.Hardware.Name : cat;
                var tile = new SensorTile(_hist, s, Hw.LoadFor(_computer, _hw, s), s.Name, sub, accent, TileSize.Medium, component: false)
                {
                    Dimmed = Hidden(s),
                };
                var sensor = s;
                var m = new ContextMenuStrip();
                m.Items.Add(Hidden(s) ? "Einblenden" : "Ausblenden", null, (_, _) => ToggleHidden(sensor));
                m.Items.Add("In der Übersicht anheften", null, (_, _) => _pin(sensor));
                m.Items.Add("Min/Max zurücksetzen", null, (_, _) => { sensor.ResetMin(); sensor.ResetMax(); });
                tile.ContextMenuStrip = m;
                _tiles.Add(tile);
                Controls.Add(tile);
                last = tile;
            }
            if (last != null) SetFlowBreak(last, true);
        }

        // ----- Alle Werte -----
        var rows = all.Where(s => _showHidden || !Hidden(s)).ToList();
        AddFull(new SectionHeader("Alle Werte", accent) { Height = S(36), Margin = new Padding(S(6), S(12), S(6), S(2)) });
        if (rows.Count > 0)
        {
            _table = new SensorTable(_hw, rows, accent) { IsHidden = Hidden };
            _table.HideToggled += ToggleHidden;
            _table.Pinned += s => _pin(s);
            AddFull(_table);
        }

        ResizeFull();
        ResumeLayout();
        try { VerticalScroll.Value = Math.Min(scroll, VerticalScroll.Maximum); PerformLayout(); } catch { }
    }

    public void RefreshValues()
    {
        foreach (var t in _tiles) t.Invalidate();
        _table?.Invalidate();
    }
}

// ===========================================================================
// Dialog "Übersicht anpassen": links alles zum Anhaken, rechts Reihenfolge & Größe
// ===========================================================================
sealed class CustomizeDialog : Form
{
    readonly IComputer _computer;
    readonly List<TileConfig> _tiles;
    readonly TreeView _tree;
    readonly ListBox _list;
    readonly ComboBox _size;
    readonly CheckedListBox _sections;
    readonly List<string> _sectionIds = new();
    bool _sync;

    public CustomizeDialog(IComputer computer)
    {
        _computer = computer;
        _tiles = UiState.Instance.Tiles.Select(t => new TileConfig { Kind = t.Kind, Id = t.Id, Size = t.Size }).ToList();

        float k = DeviceDpi / 96f;
        int S(float px) => (int)(px * k);

        Text = "Übersicht anpassen";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Header;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(S(900), S(684));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var leftTitle = new Label
        {
            Text = "Was soll in der Übersicht stehen?  (Bauteil = große Kachel mit Haupttemperatur, darunter einzelne Sensoren)",
            ForeColor = Theme.Muted, AutoSize = false,
            Bounds = new Rectangle(S(16), S(12), S(470), S(36)),
        };
        _tree = new TreeView
        {
            CheckBoxes = true,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            LineColor = Theme.Border,
            ShowLines = false,
            FullRowSelect = true,
            ItemHeight = S(24),
            Bounds = new Rectangle(S(16), S(52), S(470), S(570)),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
        };

        var rightTitle = new Label
        {
            Text = "Reihenfolge & Größe",
            ForeColor = Theme.Muted, AutoSize = false,
            Bounds = new Rectangle(S(504), S(12), S(380), S(36)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _list = new ListBox
        {
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            ItemHeight = S(22),
            Bounds = new Rectangle(S(504), S(52), S(380), S(300)),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        Button Btn(string t, int x, int y, int w)
        {
            var b = PageBar.Button(t, k, w);
            b.Location = new Point(x, y);
            b.Height = S(30);
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(b);
            return b;
        }

        int by = S(362);
        var up = Btn("▲  Nach vorne", S(504), by, 120);
        var down = Btn("▼  Nach hinten", S(630), by, 120);
        var remove = Btn("Entfernen", S(756), by, 128);
        var sizeLbl = new Label { Text = "Größe:", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(S(504), by + S(46)), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        _size = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            Bounds = new Rectangle(S(560), by + S(42), S(120), S(26)),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        _size.Items.AddRange(new object[] { "Klein", "Mittel", "Groß" });
        var secLbl = new Label
        {
            Text = "Sensoren dieser Bauteile in der Übersicht zeigen (unter den Kacheln, aufklappbar):",
            ForeColor = Theme.Muted, AutoSize = false,
            Bounds = new Rectangle(S(504), S(452), S(380), S(36)),
        };
        _sections = new CheckedListBox
        {
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            CheckOnClick = true,
            IntegralHeight = false,
            Bounds = new Rectangle(S(504), S(490), S(380), S(132)),
        };
        foreach (var hw in computer.Hardware.OrderBy(h => MainForm.Order(h.HardwareType)))
        {
            if (!MainForm.AllSensors(hw).Any(s => s.Value.HasValue)) continue;
            string id = hw.Identifier.ToString();
            _sectionIds.Add(id);
            _sections.Items.Add($"{Hw.Category(hw)} · {hw.Name}", UiState.Instance.DashboardSections.Contains(id));
        }
        Controls.Add(secLbl);
        Controls.Add(_sections);

        var reset = Btn("Standard wiederherstellen", S(16), S(638), 200);
        var ok = Btn("Übernehmen", S(668), S(638), 110);
        var cancel = Btn("Abbrechen", S(784), S(638), 100);
        reset.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        ok.Anchor = cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        ok.BackColor = Theme.Accent;
        ok.ForeColor = Color.FromArgb(10, 20, 40);

        Controls.AddRange(new Control[] { leftTitle, _tree, rightTitle, _list, sizeLbl, _size });

        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(+1);
        remove.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _tiles.RemoveAt(_list.SelectedIndex);
            RefreshAll();
        };
        _size.SelectedIndexChanged += (_, _) =>
        {
            if (_sync || _list.SelectedIndex < 0 || _size.SelectedIndex < 0) return;
            _tiles[_list.SelectedIndex].Size = (TileSize)_size.SelectedIndex;
            RefreshList(_list.SelectedIndex);
        };
        _list.SelectedIndexChanged += (_, _) =>
        {
            _sync = true;
            _size.SelectedIndex = _list.SelectedIndex >= 0 ? (int)_tiles[_list.SelectedIndex].Size : -1;
            _sync = false;
        };
        _tree.AfterCheck += (_, e) =>
        {
            if (_sync || e.Node?.Tag is not TileConfig tag) return;
            int idx = _tiles.FindIndex(t => t.Kind == tag.Kind && t.Id == tag.Id);
            if (e.Node.Checked && idx < 0) _tiles.Add(new TileConfig { Kind = tag.Kind, Id = tag.Id, Size = tag.Size });
            else if (!e.Node.Checked && idx >= 0) _tiles.RemoveAt(idx);
            RefreshList(_tiles.Count - 1);
        };
        reset.Click += (_, _) =>
        {
            _tiles.Clear();
            _tiles.AddRange(Hw.DefaultTiles(_computer));
            RefreshAll();
        };
        ok.Click += (_, _) =>
        {
            UiState.Instance.Tiles = _tiles;
            UiState.Instance.DashboardSections = _sections.CheckedIndices.Cast<int>().Select(i => _sectionIds[i]).ToList();
            UiState.Instance.TilesInitialized = true;
            UiState.Instance.Save();
            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = ok;
        CancelButton = cancel;

        BuildTree();
        RefreshList(-1);
    }

    void BuildTree()
    {
        _sync = true;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var hw in _computer.Hardware.OrderBy(h => MainForm.Order(h.HardwareType)))
        {
            var sensors = MainForm.AllSensors(hw).Where(s => s.Value.HasValue && !Hw.IsEmptyFanPort(s)).ToList();
            if (sensors.Count == 0) continue;
            string hid = hw.Identifier.ToString();
            var hn = new TreeNode($"{Hw.Category(hw)} · {hw.Name}")
            {
                Tag = new TileConfig { Kind = "hw", Id = hid, Size = TileSize.Medium },
                Checked = _tiles.Any(t => t.Kind == "hw" && t.Id == hid),
            };
            foreach (var s in sensors.OrderBy(s => SensorFormat.Rank(s.SensorType)).ThenBy(s => s.Index))
            {
                string sid = s.Identifier.ToString();
                hn.Nodes.Add(new TreeNode($"{SensorFormat.Label(s.SensorType)}:  {Hw.SensorLabel(s)}")
                {
                    Tag = new TileConfig { Kind = "sensor", Id = sid, Size = TileSize.Small },
                    Checked = _tiles.Any(t => t.Kind == "sensor" && t.Id == sid),
                });
            }
            _tree.Nodes.Add(hn);
        }
        _tree.EndUpdate();
        _sync = false;
    }

    string Describe(TileConfig t)
    {
        string name;
        if (t.Kind == "hw") name = Hw.Find(_computer, t.Id) is IHardware h ? $"{Hw.Category(h)} · {h.Name}" : "(nicht gefunden)";
        else name = Hw.FindSensor(_computer, t.Id) is { } f ? $"{Hw.SensorLabel(f.Sensor)}  ({Hw.Category(f.Top)})" : "(nicht gefunden)";
        return $"{TileSizes.Name(t.Size),-7}  {name}";
    }

    void RefreshList(int select)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var t in _tiles) _list.Items.Add(Describe(t));
        _list.EndUpdate();
        if (select >= 0 && select < _list.Items.Count) _list.SelectedIndex = select;
    }

    void RefreshAll()
    {
        BuildTree();
        RefreshList(-1);
    }

    void MoveSelected(int dir)
    {
        int i = _list.SelectedIndex, j = i + dir;
        if (i < 0 || j < 0 || j >= _tiles.Count) return;
        (_tiles[i], _tiles[j]) = (_tiles[j], _tiles[i]);
        RefreshList(j);
    }
}
