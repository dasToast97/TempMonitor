using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TempMonitor;

// ===========================================================================
// Bereich "Lüfter": Profilauswahl, Zielwerte, eine Karte pro Lüfter, Sicherheit
// ===========================================================================
sealed class FanView : FlowLayoutPanel
{
    readonly FanController _ctl;
    readonly List<ProfileButton> _buttons = new();
    readonly List<FanCard> _cards = new();
    readonly List<Control> _fullWidth = new();
    Label? _profileInfo;
    TargetsPanel? _targets;

    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public event Action? ProfileChanged;

    public FanView(FanController ctl)
    {
        _ctl = ctl;
        AutoScroll = true;
        BackColor = Theme.Background;
        Padding = new Padding(S(12), S(6), S(12), S(12));
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
            Height = S(40),
            Margin = new Padding(S(8), S(4), S(8), 0),
            Font = new Font("Segoe UI", 9.5f),
        };
        AddFull(_profileInfo);

        _targets = new TargetsPanel(_ctl) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        AddFull(_targets);
        UpdateProfileInfo();

        // ----- Lüfter -----
        string count = _ctl.HiddenCount > 0
            ? $"Lüfter & Pumpen  ·  {_ctl.Channels.Count} angeschlossen, {_ctl.HiddenCount} leere Anschlüsse ausgeblendet"
            : $"Lüfter & Pumpen  ·  {_ctl.Channels.Count} angeschlossen";
        AddSection(count, Color.FromArgb(56, 189, 248));
        if (_ctl.Channels.Count == 0)
        {
            var hint = new Label
            {
                Text = "Keine steuerbaren Lüfter gefunden.\n\n" +
                       "•  Programm als Administrator starten\n" +
                       "•  Treiber PawnIO installieren – nötig für Mainboard-Lüfter (Hinweis im Reiter „Temperaturen“)\n" +
                       "•  Im BIOS die Lüfter auf „PWM“ bzw. „DC“ stellen, nicht auf „Full Speed“\n" +
                       "•  Hersteller-Programme (Armoury Crate, MSI Center, iCUE, Fan Xpert …) schließen,\n" +
                       "    sie können die Steuerung blockieren",
                ForeColor = Theme.Muted,
                AutoSize = true,
                Font = new Font("Segoe UI", 10.5f),
                Margin = new Padding(S(10), S(10), 0, 0),
            };
            Controls.Add(hint);
            SetFlowBreak(hint, true);
        }
        else
        {
            FanCard? lastCard = null;
            foreach (var ch in _ctl.Channels)
            {
                var card = new FanCard(ch, _ctl);
                _cards.Add(card);
                Controls.Add(card);
                lastCard = card;
            }
            if (lastCard != null) SetFlowBreak(lastCard, true);
        }

        // ----- Sicherheit (optional) -----
        AddSection("Sicherheit (optional)", Theme.Hot);
        AddFull(BuildSafetyPanel());

        ApplyCustomMode();
        ResizeFullWidth();
        ResumeLayout();
    }

    void AddFull(Control c)
    {
        _fullWidth.Add(c);
        Controls.Add(c);
        SetFlowBreak(c, true);
    }

    Control BuildSafetyPanel()
    {
        var s = _ctl.Settings;
        var panel = new Panel
        {
            Height = S(118),
            BackColor = Theme.Card,
            Margin = new Padding(S(6), S(4), S(6), S(4)),
        };

        var emergency = new CheckBox
        {
            Text = "Notkühlung: alle Lüfter auf 100 %, sobald ein Bauteil seinen Grenzwert erreicht",
            Checked = s.EmergencyEnabled,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(S(14), S(12)),
        };

        var controls = new List<Control> { emergency };
        var nums = new List<NumericUpDown>();
        int x = S(36);
        foreach (var c in Components.All)
        {
            var lbl = new Label { Text = $"{Components.Name(c)} ab", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, S(43)) };
            x += TextRenderer.MeasureText(lbl.Text, lbl.Font).Width + S(6);
            var num = Ui.Number(s.Critical(c), 40, 110, new Point(x, S(40)), K);
            var comp = c;
            num.ValueChanged += (_, _) =>
            {
                float v = (float)num.Value;
                if (comp == Component.Cpu) s.CpuCritical = v;
                else if (comp == Component.Gpu) s.GpuCritical = v;
                else s.BoardCritical = v;
                _ctl.Save();
            };
            x += num.Width + S(4);
            var unit = new Label { Text = "°C", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, S(43)) };
            x += S(40);
            controls.AddRange(new Control[] { lbl, num, unit });
            nums.Add(num);
        }

        void SyncEnabled() { foreach (var n in nums) n.Enabled = emergency.Checked; }
        SyncEnabled();
        emergency.CheckedChanged += (_, _) => { s.EmergencyEnabled = emergency.Checked; SyncEnabled(); _ctl.Save(); };

        var stall = new CheckBox
        {
            Text = "Stillstand-Erkennung: Lüfter zurück ans BIOS geben, wenn er trotz Befehl nicht dreht",
            Checked = s.StallDetection,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(S(14), S(80)),
        };
        stall.CheckedChanged += (_, _) => { s.StallDetection = stall.Checked; _ctl.Save(); };
        controls.Add(stall);

        panel.Controls.AddRange(controls.ToArray());
        return panel;
    }

    void AddSection(string text, Color accent)
    {
        var h = new SectionHeader(text, accent)
        {
            Height = S(40),
            Margin = new Padding(S(6), S(14), S(6), S(4)),
        };
        AddFull(h);
    }

    void ResizeFullWidth()
    {
        int w = Math.Max(100, ClientSize.Width - Padding.Horizontal - S(16));
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
        SuspendLayout();
        UpdateProfileInfo();
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
            FanProfile.Custom => "Ziehe die Punkte der Kurve mit der Maus. Jeder Lüfter folgt dem heißesten der bei ihm angehakten Bauteile.",
            _ => "Smart-Automatik: Jeder Lüfter regelt nach allen bei ihm angehakten Bauteilen – wer gerade mehr Kühlung braucht, bestimmt. " +
                 "Sie lernt, welche Drehzahl bei welcher Auslastung nötig ist, und reagiert dadurch schon beim Lastanstieg.",
        };
        _targets?.ShowFor(p);
    }

    public void RefreshValues()
    {
        foreach (var c in _cards) c.RefreshValues();
    }
}

// ===========================================================================
// Kleine Helfer für Eingabefelder im dunklen Design
// ===========================================================================
static class Ui
{
    public static NumericUpDown Number(float value, int min, int max, Point location, float k) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = (decimal)Math.Clamp(value, min, max),
        Width = (int)(56 * k),
        Location = location,
        BackColor = Theme.CardHero,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        TextAlign = HorizontalAlignment.Right,
    };
}

// ===========================================================================
// Zielwerte je Profil: CPU / GPU / Mainboard + Mindestleistung Pumpen
// ===========================================================================
sealed class TargetsPanel : Panel
{
    readonly FanController _ctl;
    readonly Label _title;
    readonly Dictionary<Component, NumericUpDown> _nums = new();
    readonly NumericUpDown _pump;
    ProfileInfo? _profile;
    bool _loading;

    public TargetsPanel(FanController ctl)
    {
        _ctl = ctl;
        float k = DeviceDpi / 96f;
        int S(float px) => (int)(px * k);
        Height = S(84);
        BackColor = Theme.Card;

        _title = new Label
        {
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 9.5f),
            AutoSize = true,
            Location = new Point(S(14), S(10)),
        };
        Controls.Add(_title);

        int x = S(14);
        foreach (var c in Components.All)
        {
            var lbl = new Label { Text = $"Ziel {Components.Name(c)}", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, S(45)) };
            x += TextRenderer.MeasureText(lbl.Text, lbl.Font).Width + S(6);
            var num = Ui.Number(60, 30, 100, new Point(x, S(42)), k);
            x += num.Width + S(4);
            var unit = new Label { Text = "°C", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, S(45)) };
            x += S(34);
            var comp = c;
            num.ValueChanged += (_, _) =>
            {
                if (_loading || _profile is null) return;
                _ctl.Settings.TargetsFor(_profile.Profile)[(int)comp] = (float)num.Value;
                _ctl.Save();
            };
            _nums[c] = num;
            Controls.AddRange(new Control[] { lbl, num, unit });
        }

        var pumpLbl = new Label { Text = "Pumpen mindestens", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x + S(10), S(45)) };
        int px = x + S(10) + TextRenderer.MeasureText(pumpLbl.Text, pumpLbl.Font).Width + S(6);
        _pump = Ui.Number(_ctl.Settings.PumpMinPercent, 20, 100, new Point(px, S(42)), k);
        _pump.ValueChanged += (_, _) => { _ctl.Settings.PumpMinPercent = (float)_pump.Value; _ctl.Save(); };
        var pumpUnit = new Label { Text = "%", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(px + _pump.Width + S(4), S(45)) };
        Controls.AddRange(new Control[] { pumpLbl, _pump, pumpUnit });

        var reset = new LinkLabel
        {
            Text = "Standardwerte",
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Text,
            AutoSize = true,
            Location = new Point(px + _pump.Width + S(40), S(45)),
        };
        reset.Click += (_, _) =>
        {
            if (_profile is null) return;
            _ctl.Settings.Targets.Remove(_profile.Profile.ToString());
            _ctl.Save();
            ShowFor(_profile);
        };
        Controls.Add(reset);
    }

    public void ShowFor(ProfileInfo p)
    {
        _profile = p;
        Visible = p.Adaptive;
        if (!p.Adaptive) return;
        _loading = true;
        _title.Text = $"Zieltemperaturen für „{p.Title}“ – die Lüfter regeln so, dass diese Werte gehalten werden:";
        var t = _ctl.Settings.TargetsFor(p.Profile);
        foreach (var c in Components.All)
        {
            _nums[c].Value = (decimal)Math.Clamp(t[(int)c], 30, 100);
            _nums[c].Enabled = _ctl.HasComponent(c);
        }
        _loading = false;
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
            _selected ? Theme.Text : Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Info.Adaptive)
        {
            TextRenderer.DrawText(g, "SMART", TagFont,
                new Rectangle(Width - (int)(56 * k), (int)(12 * k), (int)(46 * k), (int)(20 * k)), col,
                TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        TextRenderer.DrawText(g, Info.Description, DescFont,
            new Rectangle((int)(14 * k), (int)(38 * k), Width - (int)(24 * k), (int)(34 * k)), Theme.Muted,
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }
}


// ===========================================================================
// Karte pro Lüfter / Pumpe
// ===========================================================================
sealed class FanCard : Control
{
    static readonly Font NameFont = new("Segoe UI Semibold", 11f);
    static readonly Font SmallFont = new("Segoe UI", 8.5f);
    static readonly Font BigFont = new("Segoe UI Semibold", 20f);

    readonly FanChannel _ch;
    readonly FanController _ctl;
    readonly TextBox _name;
    readonly ComboBox _kind;
    readonly Button _identify;
    readonly Dictionary<Component, CheckBox> _sources = new();
    readonly Label _sourceLabel;
    readonly CurveEditor _curve;
    bool _showCurve;

    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    const int BaseHeight = 236;

    public FanCard(FanChannel ch, FanController ctl)
    {
        _ch = ch;
        _ctl = ctl;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;

        // Name – direkt bearbeitbar
        _name = new TextBox
        {
            Text = ch.DisplayName,
            Font = NameFont,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            PlaceholderText = ch.DefaultName,
        };
        new ToolTip().SetToolTip(_name, "Anklicken zum Umbenennen, z. B. „Front oben“ oder „Radiator“");
        _name.Leave += (_, _) => SaveName();
        _name.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { SaveName(); e.SuppressKeyPress = true; Parent?.Focus(); } };

        // Lüfter oder Pumpe
        _kind = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.CardHero,
            ForeColor = Theme.Text,
        };
        _kind.Items.AddRange(new object[] { "Lüfter", "Pumpe" });
        _kind.SelectedIndex = ch.Kind == FanKind.Pump ? 1 : 0;
        _kind.SelectedIndexChanged += (_, _) =>
        {
            ch.Cfg.Kind = _kind.SelectedIndex == 1 ? FanKind.Pump : FanKind.Fan;
            ctl.Save();
            Invalidate();
        };

        _identify = new Button
        {
            Text = "Erkennen",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.CardHero,
            ForeColor = Theme.Text,
            Cursor = Cursors.Hand,
        };
        _identify.FlatAppearance.BorderColor = Theme.Border;
        new ToolTip().SetToolTip(_identify, "Lässt nur diesen Lüfter 5 Sekunden auf 100 % laufen, damit du ihn findest");
        _identify.Click += (_, _) => ctl.Identify(ch);

        _sourceLabel = new Label
        {
            Text = "Regelt nach:",
            ForeColor = Theme.Muted,
            BackColor = Theme.Card,
            AutoSize = true,
        };

        foreach (var c in Components.All)
        {
            var cb = new CheckBox
            {
                Checked = ch.Cfg.Uses(c),
                // GPU-Lüfter: nur nach der GPU regeln
                Enabled = ctl.HasComponent(c) && (!ch.IsGpu || c == Component.Gpu),
                ForeColor = Theme.Text,
                BackColor = Theme.Card,
                AutoSize = false,
            };
            var comp = c;
            cb.CheckedChanged += (_, _) =>
            {
                ch.Cfg.SetUse(comp, cb.Checked);
                ctl.Save();
            };
            _sources[c] = cb;
        }

        _curve = new CurveEditor(ch.Cfg.Curve) { Visible = false };
        _curve.CurveChanged += () => ctl.Save();

        Controls.Add(_name);
        Controls.Add(_kind);
        Controls.Add(_identify);
        Controls.Add(_sourceLabel);
        foreach (var cb in _sources.Values) Controls.Add(cb);
        Controls.Add(_curve);
        UpdateSourceTexts();
    }

    void SaveName()
    {
        string n = _name.Text.Trim();
        _ch.Cfg.CustomName = string.IsNullOrEmpty(n) || n == _ch.DefaultName ? null : n;
        if (string.IsNullOrEmpty(n)) _name.Text = _ch.DefaultName;
        _ctl.Save();
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
        Size = new Size(S(420), _showCurve ? S(BaseHeight + 186) : S(BaseHeight));
        _name.SetBounds(S(48), S(14), Width - S(210), S(24));
        _kind.SetBounds(S(16), S(62), S(96), S(26));
        _identify.SetBounds(S(120), S(61), S(100), S(28));
        _sourceLabel.Location = new Point(S(16), S(150));
        int x = S(16), w = (Width - S(32)) / 3;
        foreach (var c in Components.All)
        {
            _sources[c].SetBounds(x, S(168), w, S(24));
            x += w;
        }
        _curve.Visible = _showCurve;
        _curve.SetBounds(S(12), S(BaseHeight + 4), Width - S(24), S(176));
        Invalidate();
    }

    void UpdateSourceTexts()
    {
        foreach (var c in Components.All)
        {
            string txt = Components.Name(c);
            if (!_ctl.HasComponent(c)) txt += " (fehlt)";
            else if (_ch.IsGpu && c != Component.Gpu) txt += " (nicht für GPU)";
            else if (_ctl.Temp(c) is float t)
            {
                txt += $" {t:0}°";
                if (c != Component.Board && _ctl.Load(c) is float l) txt += $" · {l:0}%";
            }
            if (_sources[c].Text != txt) _sources[c].Text = txt;
            var col = _ch.Driver == c ? Theme.Text : Theme.Muted;
            if (_sources[c].ForeColor != col) _sources[c].ForeColor = col;
        }
    }

    public void RefreshValues()
    {
        UpdateSourceTexts();
        _curve.CurrentTemp = _ch.DriverTemp;
        _curve.CurrentOutput = _ch.Output >= 0 ? _ch.Output : null;
        if (_showCurve) _curve.Invalidate();
        Invalidate(new Rectangle(0, 0, Width, S(146)));
        Invalidate(new Rectangle(0, S(196), Width, S(40)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        bool pump = _ch.Kind == FanKind.Pump;
        var accent = pump ? Color.FromArgb(56, 189, 248)
                   : _ch.IsGpu ? Theme.ForCategory("Grafikkarte") : Theme.ForCategory("Mainboard");
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

        // Symbol: Lüfter oder Pumpe
        DrawIcon(g, new RectangleF(S(14), S(14), S(26), S(26)), pump, accent);

        const TextFormatFlags L = TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        const TextFormatFlags R = TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        string sub = (pump ? "Pumpe" : "Lüfter") + " · " + _ch.HardwareName +
                     (_ch.DisplayName != _ch.DefaultName ? $" · {_ch.DefaultName}" : "");
        TextRenderer.DrawText(g, sub, SmallFont, new Rectangle(S(48), S(40), Width - S(210), S(16)), Theme.Muted, L);

        // Drehzahl groß
        string rpm = _ch.Rpm?.Value is float r ? $"{r:N0}" : "–";
        TextRenderer.DrawText(g, rpm, BigFont, new Rectangle(Width - S(160), S(10), S(110), S(36)), Theme.Text, R);
        TextRenderer.DrawText(g, "U/min", SmallFont, new Rectangle(Width - S(48), S(22), S(36), S(16)), Theme.Muted, L);

        // Leistungsbalken
        float pct = _ch.Output >= 0 ? _ch.Output : (_ch.ControlSensor.Value ?? 0);
        bool bios = _ctl.Profile == FanProfile.Bios || _ch.Faulted || _ch.Output < 0;
        var barCol = bios ? Theme.Muted : ProfileButton.ColorFor(_ctl.Profile);
        var bar = new Rectangle(S(16), S(102), Width - S(32), S(12));
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
        TextRenderer.DrawText(g, $"Leistung {pct:0} %", SmallFont, new Rectangle(S(16), S(120), S(160), S(18)), Theme.Text, L);
        if (_ch.Driver is Component d && _ch.DriverTemp is float t && !bios)
            TextRenderer.DrawText(g, $"bestimmt von {Components.Name(d)} · {t:0} °C", SmallFont,
                new Rectangle(Width - S(236), S(120), S(220), S(18)), Theme.ForTemp(t), R);

        // Status
        var statusCol = _ch.Faulted || _ch.Status.StartsWith("⚠") ? Theme.Hot
                      : _ch.Identifying ? Theme.Ok : Theme.Muted;
        TextRenderer.DrawText(g, _ch.Status, SmallFont, new Rectangle(S(16), S(198), Width - S(32), S(34)), statusCol,
            TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    static void DrawIcon(Graphics g, RectangleF r, bool pump, Color col)
    {
        using var pen = new Pen(col, Math.Max(1.5f, r.Width / 13f));
        using var brush = new SolidBrush(col);
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        if (pump)
        {
            // Tropfen
            using var path = new GraphicsPath();
            path.AddBezier(cx, r.Top, cx + r.Width * 0.1f, r.Top + r.Height * 0.25f,
                           r.Right - r.Width * 0.12f, r.Top + r.Height * 0.45f, r.Right - r.Width * 0.12f, cy + r.Height * 0.15f);
            path.AddArc(r.X + r.Width * 0.12f, cy - r.Height * 0.2f, r.Width * 0.76f, r.Height * 0.7f, 0, 180);
            path.AddBezier(r.X + r.Width * 0.12f, cy + r.Height * 0.15f, r.X + r.Width * 0.12f, r.Top + r.Height * 0.45f,
                           cx - r.Width * 0.1f, r.Top + r.Height * 0.25f, cx, r.Top);
            g.FillPath(brush, path);
        }
        else
        {
            // Lüfter: Rahmen + drei Flügel
            g.DrawEllipse(pen, r);
            for (int i = 0; i < 3; i++)
            {
                var state = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(i * 120);
                g.FillEllipse(brush, -r.Width * 0.09f, -r.Height * 0.40f, r.Width * 0.22f, r.Height * 0.36f);
                g.Restore(state);
            }
            g.FillEllipse(brush, cx - r.Width * 0.09f, cy - r.Height * 0.09f, r.Width * 0.18f, r.Height * 0.18f);
        }
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
                    TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter);
            }
            for (int p = 0; p <= 100; p += 25)
            {
                var a = ToScreen(TMin, p);
                g.DrawLine(grid, r.Left, a.Y, r.Right, a.Y);
                TextRenderer.DrawText(g, $"{p}%", SmallFont, new Rectangle(0, (int)a.Y - 7, r.Left - 4, 14), Theme.Muted,
                    TextFormatFlags.NoPrefix | TextFormatFlags.Right);
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
