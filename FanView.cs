using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TempMonitor;

// ===========================================================================
// Bereich "Lüfter": Profilauswahl + eine Karte pro Lüfter
// ===========================================================================
sealed class FanView : FlowLayoutPanel
{
    readonly FanController _ctl;
    readonly List<ProfileButton> _buttons = new();
    readonly List<FanCard> _cards = new();
    readonly List<Control> _fullWidth = new();
    Label? _profileInfo;

    float K => DeviceDpi / 96f;

    public event Action? ProfileChanged;

    public FanView(FanController ctl)
    {
        _ctl = ctl;
        AutoScroll = true;
        BackColor = Theme.Background;
        Padding = new Padding((int)(12 * K), (int)(6 * K), (int)(12 * K), (int)(12 * K));
        Resize += (_, _) => ResizeFullWidth();
        Build();
    }

    void Build()
    {
        SuspendLayout();
        Controls.Clear();
        _buttons.Clear();
        _cards.Clear();
        _fullWidth.Clear();

        // ----- Profile -----
        AddSection("Lüfter-Profil", Theme.Accent);
        ProfileButton? last = null;
        foreach (var p in Profiles.All)
        {
            var b = new ProfileButton(p) { Selected = p.Profile == _ctl.Profile };
            b.Click += (_, _) => SelectProfile(p.Profile);
            _buttons.Add(b);
            Controls.Add(b);
            last = b;
        }
        if (last != null) SetFlowBreak(last, true);

        _profileInfo = new Label
        {
            ForeColor = Theme.Muted,
            AutoSize = false,
            Height = (int)(44 * K),
            Margin = new Padding((int)(8 * K), (int)(4 * K), (int)(8 * K), 0),
            Font = new Font("Segoe UI", 9.5f),
        };
        _fullWidth.Add(_profileInfo);
        Controls.Add(_profileInfo);
        SetFlowBreak(_profileInfo, true);
        UpdateProfileInfo();

        // ----- Sicherheit (optional) -----
        AddSection("Sicherheit (optional)", Theme.Hot);
        var safety = BuildSafetyPanel();
        _fullWidth.Add(safety);
        Controls.Add(safety);
        SetFlowBreak(safety, true);

        // ----- Lüfter -----
        AddSection("Lüfter", Color.FromArgb(56, 189, 248));
        if (_ctl.Channels.Count == 0)
        {
            var hint = new Label
            {
                Text = "Keine steuerbaren Lüfter gefunden.\n\n" +
                       "•  Programm als Administrator starten\n" +
                       "•  Treiber PawnIO installieren (https://pawnio.eu) – nötig für Mainboard-Lüfter\n" +
                       "•  Im BIOS die Lüfter auf „PWM“ bzw. „DC“ stellen, nicht auf „Full Speed“\n" +
                       "•  Hersteller-Programme (Armoury Crate, MSI Center, iCUE, Fan Xpert …) schließen,\n" +
                       "    sie können die Steuerung blockieren",
                ForeColor = Theme.Muted,
                AutoSize = true,
                Font = new Font("Segoe UI", 10.5f),
                Margin = new Padding((int)(10 * K), (int)(10 * K), 0, 0),
            };
            Controls.Add(hint);
        }
        else
        {
            foreach (var ch in _ctl.Channels)
            {
                var card = new FanCard(ch, _ctl);
                _cards.Add(card);
                Controls.Add(card);
            }
        }

        ApplyCustomMode();
        ResizeFullWidth();
        ResumeLayout();
    }

    Control BuildSafetyPanel()
    {
        var s = _ctl.Settings;
        int S(float px) => (int)(px * K);

        var panel = new Panel
        {
            Height = S(118),
            BackColor = Theme.Card,
            Margin = new Padding(S(6), S(4), S(6), S(4)),
        };

        var emergency = new CheckBox
        {
            Text = "Notkühlung: alle Lüfter auf 100 %, sobald eine Temperatur den Grenzwert erreicht",
            Checked = s.EmergencyEnabled,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(S(14), S(12)),
        };

        NumericUpDown Num(float value, int min, int max, int x) => new()
        {
            Minimum = min,
            Maximum = max,
            Value = (decimal)Math.Clamp(value, min, max),
            Width = S(58),
            Location = new Point(x, S(40)),
            BackColor = Theme.CardHero,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        Label Lbl(string text, int x) => new()
        {
            Text = text,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(x, S(43)),
        };

        var cpuLbl = Lbl("CPU ab", S(36));
        var cpu = Num(s.CpuCritical, 50, 110, S(90));
        var cpuUnit = Lbl("°C", S(152));
        var gpuLbl = Lbl("GPU ab", S(192));
        var gpu = Num(s.GpuCritical, 50, 110, S(248));
        var gpuUnit = Lbl("°C", S(310));

        void SyncEnabled() => cpu.Enabled = gpu.Enabled = emergency.Checked;
        SyncEnabled();

        emergency.CheckedChanged += (_, _) => { s.EmergencyEnabled = emergency.Checked; SyncEnabled(); _ctl.Save(); };
        cpu.ValueChanged += (_, _) => { s.CpuCritical = (float)cpu.Value; _ctl.Save(); };
        gpu.ValueChanged += (_, _) => { s.GpuCritical = (float)gpu.Value; _ctl.Save(); };

        var stall = new CheckBox
        {
            Text = "Stillstand-Erkennung: Lüfter zurück ans BIOS geben, wenn er trotz Befehl nicht dreht",
            Checked = s.StallDetection,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(S(14), S(80)),
        };
        stall.CheckedChanged += (_, _) => { s.StallDetection = stall.Checked; _ctl.Save(); };

        panel.Controls.AddRange(new Control[] { emergency, cpuLbl, cpu, cpuUnit, gpuLbl, gpu, gpuUnit, stall });
        return panel;
    }

    void AddSection(string text, Color accent)
    {
        var h = new SectionHeader(text, accent)
        {
            Height = (int)(40 * K),
            Margin = new Padding((int)(6 * K), (int)(14 * K), (int)(6 * K), (int)(4 * K)),
        };
        _fullWidth.Add(h);
        Controls.Add(h);
        SetFlowBreak(h, true);
    }

    void ResizeFullWidth()
    {
        int w = Math.Max(100, ClientSize.Width - Padding.Horizontal - (int)(16 * K));
        foreach (var c in _fullWidth) c.Width = w;
    }

    public void SelectProfile(FanProfile p)
    {
        if (p == _ctl.Profile) return;

        if (p != FanProfile.Bios && !_ctl.Settings.WarningAccepted)
        {
            var r = MessageBox.Show(FindForm(),
                "Mit der Lüftersteuerung übernimmt dieses Programm die Drehzahl deiner Lüfter.\n\n" +
                "•  Beim Beenden übernimmt automatisch wieder das BIOS\n" +
                "•  Notkühlung und Stillstand-Erkennung sind aus – du kannst sie\n" +
                "   im Abschnitt „Sicherheit“ einschalten und die Grenzwerte festlegen\n\n" +
                "Bitte behalte die Temperaturen in den ersten Minuten im Blick.\n" +
                "Die Nutzung erfolgt auf eigene Verantwortung.\n\nFortfahren?",
                "Lüftersteuerung aktivieren", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            _ctl.Settings.WarningAccepted = true;
        }

        _ctl.SetProfile(p);
        foreach (var b in _buttons) b.Selected = b.Info.Profile == p;
        UpdateProfileInfo();
        SuspendLayout();
        ApplyCustomMode();
        ResumeLayout();
        ProfileChanged?.Invoke();
    }

    void ApplyCustomMode()
    {
        bool custom = _ctl.Profile == FanProfile.Custom;
        foreach (var c in _cards) c.ShowCurve(custom);
    }

    void UpdateProfileInfo()
    {
        if (_profileInfo == null) return;
        var p = Profiles.Get(_ctl.Profile);
        _profileInfo.Text = p.Profile switch
        {
            FanProfile.Bios => "Das Programm greift nicht ein – die Lüfter laufen wie vom BIOS bzw. Grafiktreiber vorgegeben.",
            FanProfile.Max => "Alle steuerbaren Lüfter laufen mit voller Drehzahl. Maximale Kühlung, aber am lautesten.",
            FanProfile.Custom => "Ziehe die Punkte der Kurve mit der Maus: links/rechts = Temperatur, hoch/runter = Lüfterleistung. Wird sofort übernommen.",
            _ => $"Smart-Automatik: hält CPU bei ca. {p.CpuTarget:0} °C und GPU bei ca. {p.GpuTarget:0} °C. Sie lernt, welche Drehzahl bei welcher Auslastung nötig ist, " +
                 $"und reagiert dadurch schon beim Lastanstieg. Mindestleistung {p.MinPercent:0} %.",
        };
    }

    public void RefreshValues()
    {
        foreach (var c in _cards) c.RefreshValues();
    }
}

// ===========================================================================
// Profil-Kachel
// ===========================================================================
sealed class ProfileButton : Control
{
    static readonly Font TitleFont = new("Segoe UI Semibold", 10.5f);
    static readonly Font DescFont = new("Segoe UI", 8.5f);
    static readonly Font TagFont = new("Segoe UI Semibold", 7f);

    public ProfileInfo Info { get; }
    bool _selected, _hover;
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    public ProfileButton(ProfileInfo info)
    {
        Info = info;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = Theme.Background;
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        float k = DeviceDpi / 96f;
        Size = new Size((int)(196 * k), (int)(78 * k));
        Margin = new Padding((int)(6 * k));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    public static Color ColorFor(FanProfile p) => p switch
    {
        FanProfile.Bios        => Color.FromArgb(148, 163, 184),
        FanProfile.Eco         => Theme.Cool,
        FanProfile.Balanced    => Color.FromArgb(56, 189, 248),
        FanProfile.Performance => Theme.Warm,
        FanProfile.Max         => Theme.Hot,
        _                      => Color.FromArgb(167, 139, 250),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        var col = ColorFor(Info.Profile);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(r, (int)(10 * k)))
        {
            using var bg = new SolidBrush(_selected ? Color.FromArgb(38, col) : _hover ? Theme.CardHero : Theme.Card);
            g.FillPath(bg, path);
            using var pen = new Pen(_selected ? col : Theme.Border, _selected ? 2f * k : 1f);
            g.DrawPath(pen, path);
        }

        // Farbpunkt + Titel
        int d = (int)(10 * k);
        using (var b = new SolidBrush(col))
            g.FillEllipse(b, (int)(14 * k), (int)(18 * k), d, d);
        TextRenderer.DrawText(g, Info.Title, TitleFont,
            new Rectangle((int)(32 * k), (int)(10 * k), Width - (int)(40 * k), (int)(26 * k)),
            _selected ? Theme.Text : Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Info.Adaptive)
        {
            TextRenderer.DrawText(g, "SMART", TagFont,
                new Rectangle(Width - (int)(56 * k), (int)(12 * k), (int)(46 * k), (int)(20 * k)), col,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        TextRenderer.DrawText(g, Info.Description, DescFont,
            new Rectangle((int)(14 * k), (int)(38 * k), Width - (int)(24 * k), (int)(34 * k)), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }
}

// ===========================================================================
// Karte pro Lüfter
// ===========================================================================
sealed class FanCard : Control
{
    static readonly Font TitleFont = new("Segoe UI Semibold", 11f);
    static readonly Font SmallFont = new("Segoe UI", 8.5f);
    static readonly Font BigFont = new("Segoe UI Semibold", 20f);

    readonly FanChannel _ch;
    readonly FanController _ctl;
    readonly ComboBox _source;
    readonly Label _sourceLabel;
    readonly CurveEditor _curve;
    bool _showCurve;

    sealed record SourceItem(string Id, string Label) { public override string ToString() => Label; }

    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public FanCard(FanChannel ch, FanController ctl)
    {
        _ch = ch;
        _ctl = ctl;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;

        _sourceLabel = new Label
        {
            Text = "Richtet sich nach:",
            ForeColor = Theme.Muted,
            BackColor = Theme.Card,
            AutoSize = true,
        };
        _source = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.CardHero,
            ForeColor = Theme.Text,
        };
        _source.Items.Add(new SourceItem(FanController.MaxSourceId, "Höchster Wert von CPU und GPU"));
        foreach (var s in ctl.TempSensors)
            _source.Items.Add(new SourceItem(s.Identifier.ToString(), $"{s.Name}  ({s.Hardware.Name})"));
        _source.SelectedItem = _source.Items.Cast<SourceItem>().FirstOrDefault(i => i.Id == ch.Cfg.SourceId)
                               ?? _source.Items[0];
        _source.SelectedIndexChanged += (_, _) =>
        {
            if (_source.SelectedItem is SourceItem it)
            {
                ch.Cfg.SourceId = it.Id;
                ch.SmoothTemp = null;
                ch.Integral = 0;
                ctl.Save();
            }
        };

        _curve = new CurveEditor(ch.Cfg.Curve) { Visible = false };
        _curve.CurveChanged += () => ctl.Save();

        Controls.Add(_sourceLabel);
        Controls.Add(_source);
        Controls.Add(_curve);
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Margin = new Padding(S(6));
        ApplyLayout();
    }

    public void ShowCurve(bool show)
    {
        _showCurve = show;
        ApplyLayout();
    }

    void ApplyLayout()
    {
        Size = new Size(S(392), _showCurve ? S(392) : S(206));
        _sourceLabel.Location = new Point(S(16), S(166));
        _source.SetBounds(S(130), S(162), Width - S(146), S(26));
        _curve.Visible = _showCurve;
        _curve.SetBounds(S(12), S(204), Width - S(24), S(176));
        Invalidate();
    }

    public void RefreshValues()
    {
        _curve.CurrentTemp = _ch.SmoothTemp ?? _ch.SourceTemp;
        _curve.CurrentOutput = _ch.Output >= 0 ? _ch.Output : null;
        if (_showCurve) _curve.Invalidate();
        Invalidate(new Rectangle(0, 0, Width, S(156)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        var accent = _ch.IsGpu ? Theme.ForCategory("Grafikkarte") : Theme.ForCategory("Mainboard");
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(rect, S(12)))
        using (var bg = new SolidBrush(Theme.Card))
        using (var border = new Pen(Theme.Border))
        {
            g.FillPath(bg, path);
            var oldClip = g.Clip;
            g.SetClip(path);
            using (var acc = new SolidBrush(accent)) g.FillRectangle(acc, 0, 0, Width, S(3));
            g.Clip = oldClip;
            g.DrawPath(border, path);
        }

        const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        // Name + Bauteil
        TextRenderer.DrawText(g, _ch.Name, TitleFont, new Rectangle(S(16), S(12), Width - S(170), S(24)), Theme.Text, L);
        TextRenderer.DrawText(g, _ch.HardwareName, SmallFont, new Rectangle(S(16), S(36), Width - S(170), S(16)), Theme.Muted, L);

        // Drehzahl groß
        string rpm = _ch.Rpm?.Value is float r ? $"{r:N0}" : "–";
        TextRenderer.DrawText(g, rpm, BigFont, new Rectangle(Width - S(160), S(8), S(110), S(36)), Theme.Text, R);
        TextRenderer.DrawText(g, "U/min", SmallFont, new Rectangle(Width - S(48), S(20), S(36), S(16)), Theme.Muted, L);

        // Leistungsbalken
        float pct = _ch.Output >= 0 ? _ch.Output : (_ch.ControlSensor.Value ?? 0);
        bool bios = _ctl.Profile == FanProfile.Bios || _ch.Faulted || _ch.Output < 0;
        var barCol = bios ? Theme.Muted : ProfileButton.ColorFor(_ctl.Profile);
        var bar = new Rectangle(S(16), S(70), Width - S(32), S(12));
        using (var p = Gfx.RoundRect(bar, bar.Height / 2))
        using (var b = new SolidBrush(Theme.Grid))
            g.FillPath(b, p);
        int fillW = (int)(bar.Width * Math.Clamp(pct, 0, 100) / 100f);
        if (fillW > bar.Height)
        {
            var fr = new Rectangle(bar.X, bar.Y, fillW, bar.Height);
            using var p = Gfx.RoundRect(fr, bar.Height / 2);
            using var b = new SolidBrush(barCol);
            g.FillPath(b, p);
        }
        TextRenderer.DrawText(g, $"Leistung {pct:0} %", SmallFont, new Rectangle(S(16), S(88), S(160), S(18)), Theme.Text, L);
        if (_ch.SourceTemp is float t)
            TextRenderer.DrawText(g, $"Temperatur {t:0} °C", SmallFont,
                new Rectangle(Width - S(176), S(88), S(160), S(18)), Theme.ForTemp(t), R);

        // Status
        var statusCol = _ch.Faulted || _ch.Status.StartsWith("⚠") ? Theme.Hot : Theme.Muted;
        TextRenderer.DrawText(g, _ch.Status, SmallFont, new Rectangle(S(16), S(116), Width - S(32), S(34)), statusCol,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }
}

// ===========================================================================
// Kurven-Editor: Punkte mit der Maus verschieben
// ===========================================================================
sealed class CurveEditor : Control
{
    const float TMin = 20, TMax = 100;
    static readonly Font SmallFont = new("Segoe UI", 8f);

    readonly List<float[]> _pts;
    int _drag = -1, _hover = -1;

    public float? CurrentTemp { get; set; }
    public float? CurrentOutput { get; set; }
    public event Action? CurveChanged;

    public CurveEditor(List<float[]> points)
    {
        _pts = points;
        if (_pts.Count < 2)
        {
            _pts.Clear();
            _pts.AddRange(FanChannelSettings.DefaultCurve());
        }
        _pts.Sort((a, b) => a[0].CompareTo(b[0]));
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    float K => DeviceDpi / 96f;

    Rectangle Plot => new((int)(36 * K), (int)(10 * K), Width - (int)(48 * K), Height - (int)(34 * K));

    PointF ToScreen(float t, float p)
    {
        var r = Plot;
        return new PointF(r.Left + (t - TMin) / (TMax - TMin) * r.Width, r.Bottom - p / 100f * r.Height);
    }

    (float T, float P) FromScreen(Point pt)
    {
        var r = Plot;
        float t = TMin + (pt.X - r.Left) / (float)r.Width * (TMax - TMin);
        float p = (r.Bottom - pt.Y) / (float)r.Height * 100f;
        return (t, p);
    }

    int HitTest(Point pt)
    {
        for (int i = 0; i < _pts.Count; i++)
        {
            var s = ToScreen(_pts[i][0], _pts[i][1]);
            if (Math.Abs(s.X - pt.X) < 10 * K && Math.Abs(s.Y - pt.Y) < 10 * K) return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _drag = HitTest(e.Location);
        if (_drag >= 0) Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag >= 0)
        {
            var (t, p) = FromScreen(e.Location);
            float lo = _drag > 0 ? _pts[_drag - 1][0] + 1 : TMin;
            float hi = _drag < _pts.Count - 1 ? _pts[_drag + 1][0] - 1 : TMax;
            _pts[_drag][0] = (float)Math.Round(Math.Clamp(t, lo, hi));
            _pts[_drag][1] = (float)Math.Round(Math.Clamp(p, 0, 100));
            Invalidate();
        }
        else
        {
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag >= 0)
        {
            _drag = -1;
            Capture = false;
            CurveChanged?.Invoke();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = K;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Card);
        var r = Plot;

        using (var grid = new Pen(Theme.Grid) { DashStyle = DashStyle.Dot })
        {
            for (int t = 20; t <= 100; t += 10)
            {
                var a = ToScreen(t, 0);
                g.DrawLine(grid, a.X, r.Top, a.X, r.Bottom);
                TextRenderer.DrawText(g, $"{t}°", SmallFont, new Rectangle((int)a.X - 15, r.Bottom + 3, 30, 14), Theme.Muted,
                    TextFormatFlags.HorizontalCenter);
            }
            for (int p = 0; p <= 100; p += 25)
            {
                var a = ToScreen(TMin, p);
                g.DrawLine(grid, r.Left, a.Y, r.Right, a.Y);
                TextRenderer.DrawText(g, $"{p}%", SmallFont, new Rectangle(0, (int)a.Y - 7, r.Left - 4, 14), Theme.Muted,
                    TextFormatFlags.Right);
            }
        }

        // Kurve inkl. waagerechter Verlängerung an den Enden
        var pts = new List<PointF> { ToScreen(TMin, _pts[0][1]) };
        pts.AddRange(_pts.Select(p => ToScreen(p[0], p[1])));
        pts.Add(ToScreen(TMax, _pts[^1][1]));
        var col = Color.FromArgb(167, 139, 250);

        var poly = new List<PointF>(pts) { new(r.Right, r.Bottom), new(r.Left, r.Bottom) };
        using (var fill = new SolidBrush(Color.FromArgb(30, col))) g.FillPolygon(fill, poly.ToArray());
        using (var pen = new Pen(col, 2.5f * k) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts.ToArray());

        // Aktueller Arbeitspunkt
        if (CurrentTemp is float ct)
        {
            float x = ToScreen(Math.Clamp(ct, TMin, TMax), 0).X;
            using var pen = new Pen(Theme.ForTemp(ct), 1.5f * k) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, x, r.Top, x, r.Bottom);
            if (CurrentOutput is float co)
            {
                var p = ToScreen(Math.Clamp(ct, TMin, TMax), co);
                using var b = new SolidBrush(Theme.ForTemp(ct));
                g.FillEllipse(b, p.X - 4 * k, p.Y - 4 * k, 8 * k, 8 * k);
            }
        }

        // Ziehpunkte
        for (int i = 0; i < _pts.Count; i++)
        {
            var p = ToScreen(_pts[i][0], _pts[i][1]);
            float rad = (i == _hover || i == _drag ? 7 : 5.5f) * k;
            using var b = new SolidBrush(Theme.Text);
            g.FillEllipse(b, p.X - rad, p.Y - rad, rad * 2, rad * 2);
            using var pen = new Pen(col, 2f * k);
            g.DrawEllipse(pen, p.X - rad, p.Y - rad, rad * 2, rad * 2);
            if (i == _hover || i == _drag)
                TextRenderer.DrawText(g, $"{_pts[i][0]:0}° → {_pts[i][1]:0}%", SmallFont,
                    new Point((int)(p.X + 10 * k), (int)(p.Y - 20 * k)), Theme.Text);
        }
    }
}

static class Gfx
{
    public static GraphicsPath RoundRect(Rectangle r, int radius)
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
