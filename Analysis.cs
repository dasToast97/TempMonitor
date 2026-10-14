using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

// ===========================================================================
// Aufzeichnungen (CSV) lesen
// ===========================================================================
sealed class HistoryData
{
    public readonly List<DateTime> Times = new();
    public readonly Dictionary<string, List<float>> Series = new();
    public readonly List<string> Order = new();

    public List<float>? Get(string? name) => name != null && Series.TryGetValue(name, out var l) ? l : null;

    public static string Unit(string header)
    {
        int a = header.LastIndexOf('['), b = header.LastIndexOf(']');
        return a >= 0 && b > a ? header[(a + 1)..b] : "";
    }

    public static string Label(string header)
    {
        int a = header.LastIndexOf('[');
        return a > 0 ? header[..a].Trim() : header;
    }
}

static class CsvHistory
{
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static List<DateTime> Days()
    {
        var list = new List<DateTime>();
        try
        {
            if (!Directory.Exists(DataLogger.Folder)) return list;
            foreach (var f in Directory.GetFiles(DataLogger.Folder, "Toast-Temperatur_*.csv"))
            {
                var n = Path.GetFileNameWithoutExtension(f)["Toast-Temperatur_".Length..];
                if (DateTime.TryParseExact(n, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    list.Add(d);
            }
        }
        catch { }
        return list.OrderByDescending(d => d).ToList();
    }

    static string FileFor(DateTime d) => Path.Combine(DataLogger.Folder, $"Toast-Temperatur_{d:yyyy-MM-dd}.csv");

    /// Tage lesen (älteste zuerst). only = nur diese Spalten (schneller)
    public static HistoryData Load(IEnumerable<DateTime> days, ICollection<string>? only = null, CancellationToken ct = default)
    {
        var data = new HistoryData();
        foreach (var day in days.OrderBy(d => d))
        {
            string file = FileFor(day);
            if (!File.Exists(file)) continue;
            string[] cols = Array.Empty<string>();
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var rd = new StreamReader(fs, System.Text.Encoding.UTF8);
                string? line;
                while ((line = rd.ReadLine()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (line.Length == 0) continue;
                    var parts = line.Split(';');
                    if (parts[0] == "Zeit")
                    {
                        cols = parts;
                        continue;
                    }
                    if (!DateTime.TryParseExact(parts[0], "dd.MM.yyyy HH:mm:ss", De, DateTimeStyles.None, out var t)) continue;
                    int idx = data.Times.Count;
                    data.Times.Add(t);
                    for (int i = 1; i < parts.Length && i < cols.Length; i++)
                    {
                        string name = cols[i];
                        if (only != null && !only.Contains(name)) continue;
                        if (!data.Series.TryGetValue(name, out var list))
                        {
                            list = new List<float>(Math.Max(16, idx + 1024));
                            for (int k = 0; k < idx; k++) list.Add(float.NaN);
                            data.Series[name] = list;
                            data.Order.Add(name);
                        }
                        if (list.Count > idx) continue;   // doppelte Spalte
                        list.Add(parts[i].Length > 0 && float.TryParse(parts[i], NumberStyles.Float, De, out var v) ? v : float.NaN);
                    }
                    foreach (var l in data.Series.Values)
                        while (l.Count < data.Times.Count) l.Add(float.NaN);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ErrorLog.Write("Verlauf lesen", ex); }
        }
        return data;
    }
}

// ===========================================================================
// Diagramm: Zoom mit Mausrad oder Bereich ziehen, Doppelklick = alles
// ===========================================================================
sealed class HistoryChart : Control
{
    static readonly Color[] Palette =
    {
        Color.FromArgb(248, 113, 113), Color.FromArgb(96, 165, 250), Color.FromArgb(251, 191, 36), Color.FromArgb(52, 211, 153),
        Color.FromArgb(192, 132, 252), Color.FromArgb(244, 114, 182), Color.FromArgb(45, 212, 191), Color.FromArgb(251, 146, 60),
        Color.FromArgb(163, 230, 53), Color.FromArgb(148, 163, 184),
    };
    static readonly Font SmallFont = new("Segoe UI", 8.25f);

    HistoryData? _data;
    List<string> _shown = new();
    DateTime? _from, _to;
    int _hoverX = -1;
    int _dragStart = -1, _dragNow = -1;
    Point _panStart;
    DateTime _panFrom, _panTo;
    bool _panning;

    public string? EmptyText { get; set; } = "Keine Daten";

    public HistoryChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public void SetData(HistoryData? data, List<string> shown, bool keepZoom = false)
    {
        _data = data;
        _shown = shown.Where(s => data?.Series.ContainsKey(s) == true).ToList();
        if (!keepZoom) _from = _to = null;
        Invalidate();
    }

    public static Color ColorFor(int i) => Palette[i % Palette.Length];

    public void ResetZoom() { _from = _to = null; Invalidate(); }

    DateTime Start => _from ?? (_data?.Times.Count > 0 ? _data.Times[0] : DateTime.Now);
    DateTime End => _to ?? (_data?.Times.Count > 0 ? _data.Times[^1] : DateTime.Now);

    bool HasRight => _shown.Any(s => HistoryData.Unit(s) != "°C");
    bool HasLeft => _shown.Any(s => HistoryData.Unit(s) == "°C");
    Rectangle Plot => new(S(46), S(30), Width - S(46) - (HasRight ? S(54) : S(14)), Height - S(30) - S(26));

    float XOf(DateTime t)
    {
        var r = Plot;
        double span = Math.Max(1, (End - Start).TotalSeconds);
        return (float)(r.Left + (t - Start).TotalSeconds / span * r.Width);
    }

    DateTime TimeAt(int x)
    {
        var r = Plot;
        double span = Math.Max(1, (End - Start).TotalSeconds);
        return Start.AddSeconds((x - r.Left) / (double)Math.Max(1, r.Width) * span);
    }

    int IndexAt(DateTime t)
    {
        var ts = _data!.Times;
        int i = ts.BinarySearch(t);
        if (i < 0) i = ~i;
        return Math.Clamp(i, 0, ts.Count - 1);
    }

    static (float Min, float Max, float Step) Nice(float min, float max)
    {
        if (float.IsInfinity(min) || float.IsNaN(min)) { min = 0; max = 100; }
        if (max - min < 1) { max += 1; min -= 1; }
        float range = max - min;
        float[] steps = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000 };
        float step = steps.FirstOrDefault(s => range / s <= 6, 10000);
        return ((float)Math.Floor(min / step) * step, (float)Math.Ceiling(max / step) * step, step);
    }

    (float Min, float Max, float Step)? Range(bool left)
    {
        var names = _shown.Where(s => (HistoryData.Unit(s) == "°C") == left).ToList();
        if (names.Count == 0 || _data == null || _data.Times.Count == 0) return null;
        int a = IndexAt(Start), b = IndexAt(End);
        float mn = float.MaxValue, mx = float.MinValue;
        foreach (var n in names)
        {
            var l = _data.Series[n];
            for (int i = a; i <= b; i++)
            {
                float v = l[i];
                if (float.IsNaN(v)) continue;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
        }
        if (mn == float.MaxValue) return null;
        if (!left) mn = Math.Min(0, mn);
        return Nice(mn, mx);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        const TextFormatFlags C = TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        if (_data == null || _data.Times.Count < 2 || _shown.Count == 0)
        {
            TextRenderer.DrawText(g, _data == null ? EmptyText : _shown.Count == 0 ? "Links Werte auswählen" : "Zu wenig Daten",
                Font, ClientRectangle, Theme.Muted, C);
            return;
        }

        var r = Plot;
        var left = Range(true);
        var right = Range(false);
        if (left == null && right == null)
        {
            TextRenderer.DrawText(g, "Keine Werte in diesem Zeitraum", Font, ClientRectangle, Theme.Muted, C);
            return;
        }

        // Raster + Achsen
        using var grid = new Pen(Theme.Grid) { DashStyle = DashStyle.Dot };
        var main = left ?? right!.Value;
        for (float v = main.Min; v <= main.Max + 0.01f; v += main.Step)
        {
            float y = r.Bottom - (v - main.Min) / (main.Max - main.Min) * r.Height;
            g.DrawLine(grid, r.Left, y, r.Right, y);
            TextRenderer.DrawText(g, left != null ? $"{v:0}°" : $"{v:0}", SmallFont, new Rectangle(0, (int)y - S(8), r.Left - S(6), S(16)),
                Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        if (left != null && right is { } rr)
        {
            for (float v = rr.Min; v <= rr.Max + 0.01f; v += rr.Step)
            {
                float y = r.Bottom - (v - rr.Min) / (rr.Max - rr.Min) * r.Height;
                TextRenderer.DrawText(g, $"{v:0}", SmallFont, new Rectangle(r.Right + S(6), (int)y - S(8), S(50), S(16)),
                    Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        // Zeitachse
        double span = (End - Start).TotalMinutes;
        double[] stepsMin = { 1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440 };
        double st = stepsMin.FirstOrDefault(s => span / s <= Math.Max(3, r.Width / S(90)), 1440);
        var t0 = new DateTime(Start.Year, Start.Month, Start.Day).AddMinutes(Math.Ceiling((Start - Start.Date).TotalMinutes / st) * st);
        bool multiDay = Start.Date != End.Date;
        for (var t = t0; t <= End; t = t.AddMinutes(st))
        {
            float x = XOf(t);
            g.DrawLine(grid, x, r.Top, x, r.Bottom);
            string lbl = multiDay && (t.TimeOfDay == TimeSpan.Zero || st >= 360) ? t.ToString("dd.MM. HH:mm") : t.ToString("HH:mm");
            TextRenderer.DrawText(g, lbl, SmallFont, new Rectangle((int)x - S(45), r.Bottom + S(4), S(90), S(16)), Theme.Muted, C);
        }

        // Linien: pro Pixelspalte Min und Max, Lücken > 1 Minute nicht verbinden
        int a = Math.Max(0, IndexAt(Start) - 1), b = Math.Min(_data.Times.Count - 1, IndexAt(End) + 1);
        g.SetClip(r);
        for (int si = 0; si < _shown.Count; si++)
        {
            string name = _shown[si];
            var l = _data.Series[name];
            var range = HistoryData.Unit(name) == "°C" ? left : right;
            if (range is not { } rg) continue;
            using var pen = new Pen(ColorFor(_data.Order.IndexOf(name)), 1.6f * K) { LineJoin = LineJoin.Round };
            var pts = new List<PointF>();
            int col = int.MinValue;
            float cmin = 0, cmax = 0;
            DateTime last = DateTime.MinValue;

            void Flush()
            {
                if (pts.Count >= 2) g.DrawLines(pen, pts.ToArray());
                else if (pts.Count == 1) g.FillEllipse(pen.Brush, pts[0].X - 1, pts[0].Y - 1, 2, 2);
                pts.Clear();
            }
            float Y(float v) => r.Bottom - (v - rg.Min) / (rg.Max - rg.Min) * r.Height;

            for (int i = a; i <= b; i++)
            {
                float v = l[i];
                if (float.IsNaN(v)) continue;
                var t = _data.Times[i];
                if (last != DateTime.MinValue && (t - last).TotalSeconds > 90)
                {
                    if (col != int.MinValue) { pts.Add(new PointF(col, Y(cmin))); if (cmax != cmin) pts.Add(new PointF(col, Y(cmax))); }
                    col = int.MinValue;
                    Flush();
                }
                last = t;
                int x = (int)XOf(t);
                if (x != col)
                {
                    if (col != int.MinValue)
                    {
                        pts.Add(new PointF(col, Y(cmin)));
                        if (cmax != cmin) pts.Add(new PointF(col, Y(cmax)));
                    }
                    col = x; cmin = cmax = v;
                }
                else { cmin = Math.Min(cmin, v); cmax = Math.Max(cmax, v); }
            }
            if (col != int.MinValue) { pts.Add(new PointF(col, Y(cmin))); if (cmax != cmin) pts.Add(new PointF(col, Y(cmax))); }
            Flush();
        }
        g.ResetClip();

        // Legende oben
        int lx = r.Left;
        foreach (var name in _shown)
        {
            var colr = ColorFor(_data.Order.IndexOf(name));
            string txt = HistoryData.Label(name) + (HistoryData.Unit(name) is var u && u != "°C" && u.Length > 0 ? $" ({u})" : "");
            int w = TextRenderer.MeasureText(txt, SmallFont).Width;
            if (lx + w + S(20) > Width) break;
            using (var bb = new SolidBrush(colr)) g.FillEllipse(bb, lx, S(10), S(9), S(9));
            TextRenderer.DrawText(g, txt, SmallFont, new Point(lx + S(12), S(6)), Theme.Text, TextFormatFlags.NoPrefix);
            lx += w + S(26);
        }

        // Auswahl beim Ziehen
        if (_dragStart >= 0 && _dragNow >= 0)
        {
            int x1 = Math.Min(_dragStart, _dragNow), x2 = Math.Max(_dragStart, _dragNow);
            using var sel = new SolidBrush(Color.FromArgb(40, Theme.Accent));
            g.FillRectangle(sel, x1, r.Top, x2 - x1, r.Height);
        }

        // Fadenkreuz + Werte
        if (_hoverX >= r.Left && _hoverX <= r.Right && _dragStart < 0)
        {
            int i = IndexAt(TimeAt(_hoverX));
            var t = _data.Times[i];
            float x = XOf(t);
            using (var p = new Pen(Theme.Muted)) g.DrawLine(p, x, r.Top, x, r.Bottom);
            var lines = new List<(string, Color)> { (t.ToString(multiDay ? "dd.MM. HH:mm:ss" : "HH:mm:ss"), Theme.Muted) };
            foreach (var name in _shown)
            {
                float v = _data.Series[name][i];
                if (float.IsNaN(v)) continue;
                string u = HistoryData.Unit(name);
                lines.Add(($"{HistoryData.Label(name)}: {v:0.#} {u}", ColorFor(_data.Order.IndexOf(name))));
            }
            int lh = S(17);
            int bw = lines.Max(l => TextRenderer.MeasureText(l.Item1, SmallFont).Width) + S(16);
            int bh = lines.Count * lh + S(8);
            int bx = (int)x + S(12);
            if (bx + bw > Width) bx = (int)x - bw - S(12);
            int by = r.Top + S(6);
            using (var bg = new SolidBrush(Color.FromArgb(235, Theme.Background)))
            using (var bp = new Pen(Theme.Border))
            {
                g.FillRectangle(bg, bx, by, bw, bh);
                g.DrawRectangle(bp, bx, by, bw, bh);
            }
            for (int k = 0; k < lines.Count; k++)
                TextRenderer.DrawText(g, lines[k].Item1, SmallFont, new Point(bx + S(8), by + S(4) + k * lh), lines[k].Item2, TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning && _data != null)
        {
            double span = (_panTo - _panFrom).TotalSeconds;
            double dt = -(e.X - _panStart.X) / (double)Math.Max(1, Plot.Width) * span;
            var f = _panFrom.AddSeconds(dt);
            var lo = _data.Times[0]; var hi = _data.Times[^1];
            if (f < lo) f = lo;
            if (f.AddSeconds(span) > hi) f = hi.AddSeconds(-span);
            _from = f; _to = f.AddSeconds(span);
        }
        if (_dragStart >= 0) _dragNow = Math.Clamp(e.X, Plot.Left, Plot.Right);
        _hoverX = e.X;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverX = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_data == null || _data.Times.Count < 2) return;
        if (e.Button == MouseButtons.Left) { _dragStart = Math.Clamp(e.X, Plot.Left, Plot.Right); _dragNow = -1; }
        else if (e.Button == MouseButtons.Right) { _panning = true; _panStart = e.Location; _panFrom = Start; _panTo = End; Cursor = Cursors.SizeWE; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning) { _panning = false; Cursor = Cursors.Default; }
        if (_dragStart >= 0 && _dragNow >= 0 && Math.Abs(_dragNow - _dragStart) > S(6))
        {
            var t1 = TimeAt(Math.Min(_dragStart, _dragNow));
            var t2 = TimeAt(Math.Max(_dragStart, _dragNow));
            if ((t2 - t1).TotalSeconds >= 30) { _from = t1; _to = t2; }
        }
        _dragStart = _dragNow = -1;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); ResetZoom(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_data == null || _data.Times.Count < 2) return;
        var center = TimeAt(e.X);
        double f = e.Delta > 0 ? 0.7 : 1 / 0.7;
        var lo = _data.Times[0]; var hi = _data.Times[^1];
        var nf = center.AddSeconds((Start - center).TotalSeconds * f);
        var nt = center.AddSeconds((End - center).TotalSeconds * f);
        if ((nt - nf).TotalSeconds < 60) return;
        if (nf < lo) nf = lo;
        if (nt > hi) nt = hi;
        if (nf <= lo && nt >= hi) _from = _to = null;
        else { _from = nf; _to = nt; }
        Invalidate();
    }
}

// ===========================================================================
// Reiter "Analyse": Verlauf, Gesundheit, Kühlungs-Test
// ===========================================================================
sealed class AnalysisView : FlowLayoutPanel
{
    readonly List<Control> _full = new();
    readonly HistoryPanel _history;
    readonly HealthPanel _health;
    readonly CoolingTestPanel _test;
    bool _shownOnce;
    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public AnalysisView(IComputer computer, Func<KeySensors?> keys, FanController? fans)
    {
        AutoScroll = true;
        BackColor = Theme.Background;
        Padding = new Padding(S(12), S(6), S(12), S(12));
        Resize += (_, _) => ResizeFull();
        DarkMode.Use(this);
        DarkMode.NoHorizontalScroll(this);

        Section("Verlauf aus den Aufzeichnungen", Theme.Accent);
        _history = new HistoryPanel { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        AddFull(_history);

        Section("Gesundheits-Check", Theme.Ok);
        _health = new HealthPanel(computer, keys, fans) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        AddFull(_health);

        Section("Kühlungs-Test", Theme.Hot);
        _test = new CoolingTestPanel(computer, keys, fans) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        AddFull(_test);
    }

    void Section(string title, Color accent) =>
        AddFull(new SectionHeader(title, accent) { Height = S(40), Margin = new Padding(S(6), S(14), S(6), S(4)) });

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

    /// Beim Öffnen des Reiters
    public void Activate()
    {
        if (_shownOnce) { _history.ReloadIfToday(); return; }
        _shownOnce = true;
        _history.Reload();
        _health.Run();
    }

    /// Jede Sekunde – auch unsichtbar (der Kühlungs-Test misst weiter)
    public void Tick(bool visible)
    {
        _test.Tick(visible);
    }

    public bool TestRunning => _test.Running;
}

// ---------------------------------------------------------------------------
// Verlauf: Zeitraum wählen, Werte an-/abhaken, Diagramm
// ---------------------------------------------------------------------------
sealed class HistoryPanel : Panel
{
    readonly ComboBox _range;
    readonly CheckedListBox _list;
    readonly HistoryChart _chart;
    readonly Label _hint;
    readonly Button _reset, _folder;
    List<DateTime> _days = new();
    HistoryData? _data;
    CancellationTokenSource? _cts;
    bool _filling;
    DateTime _loadedAt;
    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public HistoryPanel()
    {
        BackColor = Theme.Card;
        Height = S(500);
        _range = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardHero, ForeColor = Theme.Text };
        _range.SelectedIndexChanged += (_, _) => { if (!_filling) Load(); };
        _reset = PageBar.Button("Ganz anzeigen", K, 130);
        _reset.Click += (_, _) => _chart!.ResetZoom();
        _folder = PageBar.Button("Ordner öffnen", K, 130);
        _folder.Click += (_, _) => DataLogger.OpenFolder();
        _hint = new Label
        {
            Text = "Mausrad = zoomen · Bereich ziehen = vergrößern · rechte Maustaste ziehen = verschieben · Doppelklick = alles",
            ForeColor = Theme.Muted, AutoSize = true, UseMnemonic = false,
        };
        _list = new CheckedListBox
        {
            BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, CheckOnClick = true,
            IntegralHeight = false,
        };
        DarkMode.Use(_list);
        _list.ItemCheck += (_, e) =>
        {
            if (_filling) return;
            BeginInvoke(() =>
            {
                var shown = _list.CheckedItems.Cast<SeriesItem>().Select(i => i.Name).ToList();
                AppSettings.Instance.HistorySeries = shown;
                AppSettings.Instance.Save();
                _chart!.SetData(_data, shown, keepZoom: true);
            });
        };
        _chart = new HistoryChart();
        Controls.AddRange(new Control[] { _range, _reset, _folder, _hint, _list, _chart });
        Resize += (_, _) => DoLayout();
    }

    sealed record SeriesItem(string Name)
    {
        public override string ToString() => HistoryData.Label(Name) + (HistoryData.Unit(Name) is var u && u.Length > 0 ? $"  [{u}]" : "");
    }

    void DoLayout()
    {
        int y = S(12);
        _range.SetBounds(S(14), y + S(2), S(200), S(26));
        _reset.SetBounds(S(226), y, _reset.Width, S(30));
        _folder.SetBounds(_reset.Right + S(8), y, _folder.Width, S(30));
        _hint.Location = new Point(_folder.Right + S(14), y + S(7));
        _hint.Visible = _hint.Right < Width;
        int top = y + S(42);
        _list.SetBounds(S(10), top, S(250), Height - top - S(10));
        _chart.SetBounds(S(268), top, Width - S(278), Height - top - S(10));
    }

    public void Reload()
    {
        _days = CsvHistory.Days();
        _filling = true;
        _range.Items.Clear();
        _range.Items.Add("Letzte 7 Tage");
        foreach (var d in _days)
            _range.Items.Add(d == DateTime.Today ? $"Heute ({d:dd.MM.yyyy})" : d == DateTime.Today.AddDays(-1) ? $"Gestern ({d:dd.MM.yyyy})" : d.ToString("dddd, dd.MM.yyyy"));
        _range.SelectedIndex = _days.Count > 0 ? 1 : 0;
        _filling = false;
        Load();
    }

    /// Beim erneuten Öffnen: heutigen Tag neu laden, damit die neuesten Werte dabei sind
    public void ReloadIfToday()
    {
        if ((DateTime.Now - _loadedAt).TotalMinutes >= 2 && _range.SelectedIndex <= 1) Load();
    }

    async void Load()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int sel = _range.SelectedIndex;
        var days = sel <= 0 ? _days.Where(d => d >= DateTime.Today.AddDays(-6)).ToList()
                 : sel - 1 < _days.Count ? new List<DateTime> { _days[sel - 1] } : new List<DateTime>();
        _chart.EmptyText = days.Count == 0 ? "Noch keine Aufzeichnungen. Aufzeichnung im Reiter Einstellungen einschalten." : "Lade …";
        _chart.SetData(null, new());
        try
        {
            var data = await Task.Run(() => CsvHistory.Load(days, null, cts.Token));
            if (cts.IsCancellationRequested) return;
            _data = data;
            _loadedAt = DateTime.Now;
            FillList();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _chart.EmptyText = "Fehler beim Lesen: " + ex.Message;
            _chart.Invalidate();
        }
    }

    void FillList()
    {
        var want = AppSettings.Instance.HistorySeries;
        if (want.Count == 0)
        {
            var k = AppSettings.Instance.LogKeys;
            want = new[] { "Cpu", "Gpu", "Hotspot" }.Where(k.ContainsKey).Select(x => k[x]).ToList();
            if (want.Count == 0 && _data != null) want = _data.Order.Where(o => HistoryData.Unit(o) == "°C").Take(3).ToList();
        }
        _filling = true;
        _list.Items.Clear();
        if (_data != null)
            foreach (var name in _data.Order)
                _list.Items.Add(new SeriesItem(name), want.Contains(name));
        _filling = false;
        var shown = _list.CheckedItems.Cast<SeriesItem>().Select(i => i.Name).ToList();
        _chart.EmptyText = _data == null || _data.Times.Count == 0 ? "Für diesen Zeitraum gibt es keine Werte." : "Keine Daten";
        _chart.SetData(_data, shown);
    }
}

// ---------------------------------------------------------------------------
// Gesundheits-Check: Trends aus den Aufzeichnungen + SSD-Werte
// ---------------------------------------------------------------------------
sealed class HealthPanel : Panel
{
    enum Level { Good, Watch, Bad, Info }
    sealed record Finding(Level Level, string Title, string Text);

    readonly IComputer _computer;
    readonly Func<KeySensors?> _keys;
    readonly FanController? _fans;
    readonly Button _run;
    readonly Label _summary;
    List<Finding> _items = new();
    bool _busy;
    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);
    static readonly Font TitleFont = new("Segoe UI Semibold", 10f);
    static readonly Font TextFont = new("Segoe UI", 9f);

    public HealthPanel(IComputer computer, Func<KeySensors?> keys, FanController? fans)
    {
        _computer = computer;
        _keys = keys;
        _fans = fans;
        BackColor = Theme.Card;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _run = PageBar.Button("Erneut prüfen", K, 150);
        _run.Click += (_, _) => Run();
        _summary = new Label
        {
            ForeColor = Theme.Muted, AutoSize = true, UseMnemonic = false,
            Text = "Wertet die Aufzeichnungen der letzten 14 Tage aus: Temperaturen unter Last, Abstand GPU ↔ Hot Spot, SSDs, Lüfter.",
        };
        Controls.Add(_run);
        Controls.Add(_summary);
        Height = S(120);
        Resize += (_, _) => DoLayout();
    }

    int RowsTop => S(58);

    void DoLayout()
    {
        _run.Location = new Point(S(14), S(12));
        _run.Height = S(30);
        _summary.Location = new Point(_run.Right + S(14), S(19));
        int h = RowsTop;
        foreach (var f in _items) h += RowHeight(f);
        h += S(10);
        if (Height != h) Height = h;
        Invalidate();
    }

    int RowHeight(Finding f)
    {
        int w = Math.Max(S(200), Width - S(60));
        int th = TextRenderer.MeasureText(f.Text, TextFont, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        return S(24) + th + S(10);
    }

    public async void Run()
    {
        if (_busy) return;
        _busy = true;
        _run.Enabled = false;
        _summary.Text = "Prüfe …";
        try
        {
            var live = LiveChecks();
            var hist = await Task.Run(HistoryChecks);
            _items = hist.Concat(live).ToList();
            int bad = _items.Count(i => i.Level == Level.Bad), watch = _items.Count(i => i.Level == Level.Watch);
            _summary.Text = bad > 0 ? $"{bad} Punkt(e) brauchen Aufmerksamkeit."
                          : watch > 0 ? $"Alles in Ordnung – {watch} Punkt(e) im Auge behalten."
                          : "Alles in Ordnung.";
            _summary.Text += $"   (geprüft {DateTime.Now:HH:mm})";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Gesundheits-Check", ex);
            _summary.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            _busy = false;
            _run.Enabled = true;
            DoLayout();
        }
    }

    static float Median(List<float> v)
    {
        if (v.Count == 0) return float.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    List<Finding> HistoryChecks()
    {
        var res = new List<Finding>();
        var keys = AppSettings.Instance.LogKeys;
        var days = CsvHistory.Days().Where(d => d >= DateTime.Today.AddDays(-13)).ToList();
        if (days.Count == 0 || keys.Count == 0)
        {
            res.Add(new(Level.Info, "Noch keine Aufzeichnungen",
                "Für Trends braucht der Check ein paar Tage Aufzeichnung (Reiter Einstellungen → Aufzeichnung). Die SSD-Werte unten sind aktuell."));
            return res;
        }
        var data = CsvHistory.Load(days, keys.Values.ToHashSet());
        var times = data.Times;

        // Pro Tag den Median der Werte unter Last (≥ 80 %)
        List<(DateTime Day, float Value, int N)> PerDay(string tempKey, string loadKey, Func<int, float>? value = null)
        {
            var t = data.Get(keys.GetValueOrDefault(tempKey));
            var l = data.Get(keys.GetValueOrDefault(loadKey));
            var outp = new List<(DateTime, float, int)>();
            if (t == null) return outp;
            foreach (var grp in Enumerable.Range(0, times.Count).GroupBy(i => times[i].Date))
            {
                var vals = new List<float>();
                foreach (int i in grp)
                {
                    bool loaded = l != null ? !float.IsNaN(l[i]) && l[i] >= 80 : !float.IsNaN(t[i]);
                    if (!loaded) continue;
                    float v = value != null ? value(i) : t[i];
                    if (!float.IsNaN(v)) vals.Add(v);
                }
                if (vals.Count >= 24) outp.Add((grp.Key, Median(vals), vals.Count));
            }
            return outp;
        }

        string Trend(List<(DateTime Day, float Value, int N)> d, out float delta)
        {
            delta = 0;
            if (d.Count < 3) return "";
            float first = d.Take(Math.Max(1, d.Count / 3)).Average(x => x.Value);
            float last = d.Skip(d.Count - Math.Max(1, d.Count / 3)).Average(x => x.Value);
            delta = last - first;
            return delta >= 1 ? $" Trend: +{delta:0.0} °C seit {d[0].Day:dd.MM.}" : delta <= -1 ? $" Trend: {delta:0.0} °C seit {d[0].Day:dd.MM.}" : " Trend: gleichbleibend.";
        }

        // CPU unter Last
        var cpu = PerDay("Cpu", "CpuLoad");
        if (cpu.Count > 0)
        {
            float now = cpu[^1].Value;
            string tr = Trend(cpu, out float dlt);
            var lvl = now >= 90 || dlt >= 8 ? Level.Bad : now >= 80 || dlt >= 5 ? Level.Watch : Level.Good;
            res.Add(new(lvl, $"Prozessor unter Last: {now:0} °C",
                $"Typische CPU-Temperatur bei über 80 % Auslastung (an {cpu.Count} Tagen gemessen).{tr}" +
                (lvl != Level.Good ? " Steigt sie über Wochen, sind oft Staub in Kühler/Filtern oder alternde Wärmeleitpaste die Ursache." : "")));
        }
        else res.Add(new(Level.Info, "Prozessor unter Last", "Noch zu wenig Messungen mit hoher Auslastung (Spiel oder Kühlungs-Test laufen lassen)."));

        // GPU unter Last
        var gpu = PerDay("Gpu", "GpuLoad");
        if (gpu.Count > 0)
        {
            float now = gpu[^1].Value;
            string tr = Trend(gpu, out float dlt);
            var lvl = now >= 87 || dlt >= 8 ? Level.Bad : now >= 80 || dlt >= 5 ? Level.Watch : Level.Good;
            res.Add(new(lvl, $"Grafikkarte unter Last: {now:0} °C", $"Typische GPU-Temperatur bei über 80 % Auslastung (an {gpu.Count} Tagen).{tr}"));
        }

        // Abstand GPU ↔ Hot Spot
        var g = data.Get(keys.GetValueOrDefault("Gpu"));
        var h = data.Get(keys.GetValueOrDefault("Hotspot"));
        if (g != null && h != null)
        {
            var gap = PerDay("Hotspot", "GpuLoad", i => h[i] - g[i]);
            if (gap.Count > 0)
            {
                float now = gap[^1].Value;
                string tr = Trend(gap, out float dlt);
                var lvl = now >= 30 || dlt >= 6 ? Level.Bad : now >= 22 || dlt >= 4 ? Level.Watch : Level.Good;
                res.Add(new(lvl, $"Abstand GPU ↔ Hot Spot: {now:0} °C",
                    $"Unter Last ist der heißeste Punkt des Grafikchips {now:0} °C wärmer als der Durchschnitt.{tr} " +
                    (lvl == Level.Good ? "Normal sind etwa 10–20 °C."
                     : "Normal sind etwa 10–20 °C. Ein großer oder wachsender Abstand deutet auf ausgetrocknete Wärmeleitpaste oder schlecht sitzenden Kühler hin.")));
            }
        }

        // Mainboard: Höchstwert
        var b = data.Get(keys.GetValueOrDefault("Board"));
        if (b != null)
        {
            float max = b.Where(v => !float.IsNaN(v)).DefaultIfEmpty(float.NaN).Max();
            if (!float.IsNaN(max))
            {
                var lvl = max >= 75 ? Level.Bad : max >= 65 ? Level.Watch : Level.Good;
                res.Add(new(lvl, $"Mainboard: höchstens {max:0} °C", "Höchster Wert der letzten 14 Tage." +
                    (lvl != Level.Good ? " Mehr Luftstrom im Gehäuse (Front-/Hecklüfter) hilft, vor allem bei warmen Spannungswandlern." : "")));
            }
        }
        return res;
    }

    List<Finding> LiveChecks()
    {
        var res = new List<Finding>();
        foreach (var hw in _computer.Hardware.Where(x => x.HardwareType == HardwareType.Storage))
        {
            var sensors = MainForm.AllSensors(hw).ToList();
            float? life = null;
            var rem = sensors.FirstOrDefault(s => s.SensorType == SensorType.Level && s.Name.Contains("Remaining Life", StringComparison.OrdinalIgnoreCase));
            var used = sensors.FirstOrDefault(s => s.SensorType == SensorType.Level && s.Name.Contains("Percentage Used", StringComparison.OrdinalIgnoreCase));
            if (rem?.Value is float r) life = r;
            else if (used?.Value is float u) life = Math.Max(0, 100 - u);
            var temp = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 120).Select(s => s.Value!.Value).DefaultIfEmpty(float.NaN).Max();
            var written = sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name.Contains("Written", StringComparison.OrdinalIgnoreCase))?.Value;
            var errors = sensors.FirstOrDefault(s => s.Name.Contains("Media Errors", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Reallocated", StringComparison.OrdinalIgnoreCase))?.Value;

            if (life == null && float.IsNaN(temp)) continue;
            var parts = new List<string>();
            if (life is float lf) parts.Add($"Restlebensdauer {lf:0} %");
            if (written is float w) parts.Add(w >= 1000 ? $"{w / 1000:0.0} TB geschrieben" : $"{w:0} GB geschrieben");
            if (!float.IsNaN(temp)) parts.Add($"jetzt {temp:0} °C");
            if (errors is float er && er > 0) parts.Add($"{er:0} Fehler/umgelagerte Sektoren");
            var lvl = life is < 10 || errors is > 0 ? Level.Bad : life is < 30 || temp >= 70 ? Level.Watch : Level.Good;
            string extra = lvl == Level.Bad ? " Wichtige Daten sichern – das Laufwerk nähert sich dem Ende."
                         : temp >= 70 ? " Recht warm: ein Kühlkörper oder etwas Luftstrom über der SSD hilft." : "";
            res.Add(new(lvl, hw.Name, string.Join(" · ", parts) + "." + extra));
        }

        if (_fans != null)
        {
            var stuck = _fans.Channels.Where(c => c.NotControllable).Select(c => c.DisplayName).ToList();
            if (stuck.Count > 0)
                res.Add(new(Level.Watch, "Lüfter ohne Steuerung", $"{string.Join(", ", stuck)} reagiert nicht auf Befehle und wird vom BIOS/Treiber geregelt."));
            var uncal = _fans.Calibratable.Where(c => c.Cfg.CalibratedAt == null && c.Kind == FanKind.Fan).ToList();
            if (uncal.Count > 0)
                res.Add(new(Level.Info, "Lüfter noch nicht kalibriert",
                    $"{uncal.Count} Lüfter. Die Kalibrierung (Reiter Lüfter) findet heraus, ab wie viel Prozent sie sicher anlaufen."));
        }
        return res;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        int y = RowsTop;
        int w = Math.Max(S(200), Width - S(60));
        foreach (var f in _items)
        {
            var col = f.Level switch { Level.Good => Theme.Ok, Level.Watch => Theme.Warm, Level.Bad => Theme.Hot, _ => Theme.Muted };
            using (var b = new SolidBrush(col)) g.FillEllipse(b, S(16), y + S(5), S(11), S(11));
            TextRenderer.DrawText(g, f.Title, TitleFont, new Point(S(38), y), Theme.Text, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, f.Text, TextFont, new Rectangle(S(38), y + S(22), w, Height), Theme.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += RowHeight(f);
        }
    }
}

// ---------------------------------------------------------------------------
// Kühlungs-Test: Prozessor voll auslasten, messen, mit früheren Läufen vergleichen
// ---------------------------------------------------------------------------
sealed class CoolingResult
{
    public DateTime When { get; set; }
    public string Profile { get; set; } = "";
    public int Seconds { get; set; }
    public float Start { get; set; }
    public float Max { get; set; }
    public float Steady { get; set; }
    public float? Power { get; set; }
    public float? ClockDrop { get; set; }
    public float? FanRpm { get; set; }
    public string? Aborted { get; set; }
}

sealed class CoolingTestPanel : Panel
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "kuehlungstest.json");

    readonly IComputer _computer;
    readonly Func<KeySensors?> _keys;
    readonly FanController? _fans;
    readonly Label _note, _live, _result;
    readonly ComboBox _duration;
    readonly Button _start;
    readonly HistoryChart _chart;
    readonly List<CoolingResult> _past;

    CancellationTokenSource? _cts;
    DateTime _began;
    int _seconds;
    readonly HistoryData _data = new();
    float? _clockStart;
    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    const string T = "CPU [°C]", P = "CPU-Leistung [W]", F = "CPU-Lüfter [U/min]";

    public bool Running => _cts != null;

    public CoolingTestPanel(IComputer computer, Func<KeySensors?> keys, FanController? fans)
    {
        _computer = computer;
        _keys = keys;
        _fans = fans;
        _past = LoadPast();
        BackColor = Theme.Card;

        _note = new Label
        {
            ForeColor = Theme.Muted, AutoSize = true, UseMnemonic = false,
            Text = "Lastet alle Prozessorkerne voll aus und misst, wie gut die Kühlung mithält: Starttemperatur, Höchstwert, " +
                   "Dauertemperatur, Leistung und ob der Prozessor wegen Hitze heruntertaktet. Die Lüfter laufen dabei nach dem aktuellen Profil. " +
                   "Bei 95 °C bricht der Test von selbst ab. Ergebnisse werden gespeichert, damit du später vergleichen kannst (z. B. nach dem Entstauben).",
        };
        _duration = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardHero, ForeColor = Theme.Text };
        _duration.Items.AddRange(new object[] { "3 Minuten", "5 Minuten", "10 Minuten" });
        _duration.SelectedIndex = 1;
        _start = PageBar.Button("Test starten", K, 150);
        _start.Click += (_, _) => { if (Running) Stop("vom Benutzer abgebrochen"); else Start(); };
        _live = new Label { ForeColor = Theme.Text, AutoSize = true, UseMnemonic = false };
        _result = new Label { ForeColor = Theme.Text, AutoSize = true, UseMnemonic = false, Font = new Font("Segoe UI", 9.5f) };
        _chart = new HistoryChart { EmptyText = "Hier erscheint der Verlauf während des Tests." };
        Controls.AddRange(new Control[] { _note, _duration, _start, _live, _result, _chart });
        Resize += (_, _) => DoLayout();
        ShowPast();
    }

    void DoLayout()
    {
        int w = Math.Max(S(300), Width - S(28));
        _note.Location = new Point(S(14), S(12));
        _note.MaximumSize = new Size(w, 0);
        int y = _note.Bottom + S(10);
        _duration.SetBounds(S(14), y + S(2), S(120), S(26));
        _start.SetBounds(S(144), y, _start.Width, S(30));
        _live.Location = new Point(_start.Right + S(14), y + S(7));
        y += S(42);
        _chart.SetBounds(S(10), y, Width - S(20), S(220));
        y += S(228);
        _result.Location = new Point(S(14), y);
        _result.MaximumSize = new Size(w, 0);
        int h = _result.Bottom + S(14);
        if (Height != h) Height = h;
    }

    static List<CoolingResult> LoadPast()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<CoolingResult>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    void SavePast()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_past, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    IHardware? Cpu => _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

    float? Power() => Cpu is IHardware c
        ? MainForm.AllSensors(c).FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))?.Value
        : null;

    float? Clock()
    {
        if (Cpu is not IHardware c) return null;
        var v = MainForm.AllSensors(c).Where(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && s.Value > 0)
            .Select(s => s.Value!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    float? FanRpm()
    {
        if (_fans == null) return null;
        var v = _fans.Channels.Where(c => !c.IsGpu && c.Cfg.UseCpu && c.Rpm?.Value > 0).Select(c => c.Rpm!.Value!.Value).ToList();
        if (v.Count == 0) v = _fans.Channels.Where(c => !c.IsGpu && c.Rpm?.Value > 0).Select(c => c.Rpm!.Value!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    void Start()
    {
        if (_keys()?.Cpu == null)
        {
            MessageBox.Show(FindForm(), "Die CPU-Temperatur ist nicht verfügbar (Treiber PawnIO fehlt?). Ohne sie kann der Test nicht messen.",
                "Kühlungs-Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(FindForm(),
                $"Der Prozessor wird jetzt {_duration.Text} lang voll ausgelastet. Der PC kann in der Zeit langsam reagieren.\n\n" +
                "Abbruch jederzeit mit „Test abbrechen“, automatisch bei 95 °C.\n\nStarten?",
                "Kühlungs-Test", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _seconds = _duration.SelectedIndex switch { 0 => 180, 2 => 600, _ => 300 };
        _data.Times.Clear();
        _data.Series.Clear();
        _data.Order.Clear();
        foreach (var n in new[] { T, P, F }) { _data.Series[n] = new(); _data.Order.Add(n); }
        _clockStart = null;
        _began = DateTime.Now;
        _cts = new CancellationTokenSource();
        var tok = _cts.Token;

        // Ein Rechen-Thread pro logischem Kern
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            new Thread(() =>
            {
                double x = 1.0001;
                while (!tok.IsCancellationRequested)
                    for (int k = 0; k < 200_000; k++) x = Math.Sqrt(x * 1.0000001 + k) * Math.Sin(x) + 1.0001;
                GC.KeepAlive(x);
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Kühlungs-Test" }.Start();
        }
        _start.Text = "Test abbrechen";
        _duration.Enabled = false;
        _result.Text = "Test läuft …";
        Record();
        DoLayout();
    }

    void Record()
    {
        _data.Times.Add(DateTime.Now);
        _data.Series[T].Add(_keys()?.Cpu?.Value ?? float.NaN);
        _data.Series[P].Add(Power() ?? float.NaN);
        _data.Series[F].Add(FanRpm() ?? float.NaN);
    }

    public void Tick(bool visible)
    {
        if (!Running) return;
        Record();
        double el = (DateTime.Now - _began).TotalSeconds;
        if (el >= 10 && el <= 30 && Clock() is float ck) _clockStart = _clockStart is float c0 ? Math.Max(c0, ck) : ck;

        float t = _data.Series[T][^1];
        if (!float.IsNaN(t) && t >= 95) { Stop("95 °C erreicht – die Kühlung reicht für Dauerlast nicht aus"); return; }
        if (el >= _seconds) { Stop(null); return; }

        if (visible)
        {
            var p = _data.Series[P][^1];
            _live.Text = $"{(int)el / 60}:{(int)el % 60:00} von {_seconds / 60}:00  ·  CPU {t:0} °C" +
                         (float.IsNaN(p) ? "" : $"  ·  {p:0} W") +
                         (_data.Series[F][^1] is var f && !float.IsNaN(f) ? $"  ·  Lüfter {f:N0} U/min" : "");
            _chart.SetData(_data, new() { T, P }, keepZoom: false);
        }
    }

    void Stop(string? reason)
    {
        _cts?.Cancel();
        _cts = null;
        _start.Text = "Test starten";
        _duration.Enabled = true;
        _live.Text = "";

        var temps = _data.Series[T];
        var valid = temps.Where(v => !float.IsNaN(v)).ToList();
        int el = (int)(DateTime.Now - _began).TotalSeconds;
        if (valid.Count < 20)
        {
            _result.Text = "Zu kurz für ein Ergebnis.";
            DoLayout();
            return;
        }
        float Avg(List<float> l, int lastN) => l.Skip(Math.Max(0, l.Count - lastN)).Where(v => !float.IsNaN(v)).DefaultIfEmpty(float.NaN).Average();
        float pw = Avg(_data.Series[P], 60), fan = Avg(_data.Series[F], 60);
        float? drop = _clockStart is float c0 && Clock() is float c1 && c0 > 0 ? (c0 - c1) / c0 * 100 : null;

        var r = new CoolingResult
        {
            When = DateTime.Now,
            Profile = _fans != null ? Profiles.Get(_fans.Profile).Title : "–",
            Seconds = el,
            Start = valid[0],
            Max = valid.Max(),
            Steady = Avg(temps, 60),
            Power = float.IsNaN(pw) ? null : pw,
            FanRpm = float.IsNaN(fan) ? null : fan,
            ClockDrop = drop,
            Aborted = reason,
        };
        if (reason != "vom Benutzer abgebrochen")
        {
            _past.Add(r);
            SavePast();
        }
        _chart.SetData(_data, new() { T, P });
        ShowPast(r);
    }

    void ShowPast(CoolingResult? now = null)
    {
        var sb = new System.Text.StringBuilder();
        if (now != null)
        {
            string verdict = now.Aborted != null && now.Aborted != "vom Benutzer abgebrochen" ? "⚠ " + now.Aborted
                : now.ClockDrop is > 10 ? $"⚠ Der Prozessor hat um {now.ClockDrop:0} % heruntergetaktet – vermutlich wegen Hitze oder Leistungsgrenze."
                : now.Steady >= 90 ? "Grenzwertig: unter Dauerlast sehr warm. Mehr Lüfterleistung, Entstauben oder neue Wärmeleitpaste helfen."
                : now.Steady >= 80 ? "In Ordnung, aber warm."
                : "Gut – die Kühlung hält locker mit.";
            sb.AppendLine($"Ergebnis ({now.Seconds / 60}:{now.Seconds % 60:00} min, Profil „{now.Profile}“):");
            sb.AppendLine($"Start {now.Start:0} °C  →  höchstens {now.Max:0} °C  ·  Dauertemperatur (letzte Minute) {now.Steady:0} °C" +
                          (now.Power is float p ? $"  ·  {p:0} W" : "") + (now.FanRpm is float f ? $"  ·  Lüfter {f:N0} U/min" : "") +
                          (now.ClockDrop is float d ? $"  ·  Takt {(d > 0 ? "-" : "+")}{Math.Abs(d):0} %" : ""));
            sb.AppendLine(verdict);
            var prev = _past.Where(x => x != now && x.Profile == now.Profile && x.Aborted == null).LastOrDefault();
            if (prev != null && now.Aborted == null)
            {
                float dd = now.Steady - prev.Steady;
                sb.AppendLine($"Vergleich mit {prev.When:dd.MM.yyyy} (gleiches Profil): Dauertemperatur {(dd >= 0 ? "+" : "")}{dd:0.0} °C" +
                              (dd >= 4 ? " – deutlich wärmer als damals." : dd <= -4 ? " – deutlich kühler als damals." : "."));
            }
            sb.AppendLine();
        }
        var list = _past.TakeLast(6).Reverse().ToList();
        if (list.Count > 0)
        {
            sb.AppendLine("Frühere Tests:");
            foreach (var x in list)
                sb.AppendLine($"   {x.When:dd.MM.yyyy HH:mm}  ·  {x.Profile}  ·  Start {x.Start:0} °C, max {x.Max:0} °C, Dauer {x.Steady:0} °C" +
                              (x.Power is float p ? $", {p:0} W" : "") + (x.Aborted != null ? "  (abgebrochen)" : ""));
        }
        else if (now == null) sb.Append("Noch keine Tests.");
        _result.Text = sb.ToString().TrimEnd();
        DoLayout();
    }
}
