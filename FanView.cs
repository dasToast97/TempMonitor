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
    CalibrationPanel? _calib;
    Label? _oasisState;
    Label? _laptopState;

    float K => Dpi.K(this);
    int S(float px) => (int)(px * K);

    public event Action? ProfileChanged;

    public FanView(FanController ctl)
    {
        _ctl = ctl;
        AutoScroll = true;
        BackColor = Theme.Background;
        Padding = new Padding(S(12), S(6), S(12), S(12));
        Resize += (_, _) => ResizeFullWidth();
        DarkMode.Use(this);
        DarkMode.NoHorizontalScroll(this);
        Build();
    }

    /// Nach Gruppen-Änderungen alles neu aufbauen (Scroll-Position bleibt)
    bool _wizardRunning;

    /// Lüfter-Assistent: jeden steuerbaren Anschluss nacheinander „erkennen“ und zuordnen lassen
    async Task RunWizard()
    {
        if (_wizardRunning) return;
        if (!_ctl.SafeToStop(out var why))
        {
            MessageBox.Show(FindForm(), $"Der Assistent hält Lüfter kurz an – das geht nur, wenn der PC kühl ist. Gerade {why}.\n" +
                "Bitte warten, bis er sich abgekühlt hat (z. B. kein Spiel offen).", "Lüfter-Assistent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var list = _ctl.Channels.Where(c => !c.IsGpu && !c.MonitorOnly && c.Ext == null).ToList();
        if (MessageBox.Show(FindForm(),
                $"{list.Count} Anschlüsse werden nacheinander je 5–7 Sekunden angehalten (oder hochgedreht, wenn sie gerade stehen). " +
                "Schau dabei in den PC und sag danach, was sich bewegt hat.\n\n" +
                "Schutz: Wird es dabei wärmer, bricht Toast Temperatur sofort ab und kühlt voll. Pumpen werden nur kurz angehalten. " +
                "Trotzdem gilt: Benutzung auf eigene Gefahr.\n\nStarten?",
                "Lüfter-Assistent", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        _wizardRunning = true;
        try
        {
            for (int i = 0; i < list.Count; i++)
            {
                var ch = list[i];
                if (!_ctl.SafeToStop(out var w2))
                {
                    MessageBox.Show(FindForm(), $"Assistent pausiert: {w2}. Bitte später fortsetzen.", "Lüfter-Assistent");
                    break;
                }
                ch.InWizard = true;
                _ctl.Identify(ch);
                // warten, bis das Erkennen fertig ist (max. 20 s)
                for (int t = 0; t < 200 && !ch.IdentifyFinished; t++) await Task.Delay(100);
                ch.IdentifyFinished = false;
                var res = FanCard.AskRole(ch, _ctl, FindForm()!, $"Anschluss {i + 1} von {list.Count}");
                ch.InWizard = false;
                if (res == null) break;
            }
        }
        finally
        {
            foreach (var c in list) c.InWizard = false;
            _wizardRunning = false;
            Rebuild();
        }
    }

    public void Rebuild()
    {
        var pos = AutoScrollPosition;
        Build();
        AutoScrollPosition = new Point(-pos.X, -pos.Y);
    }

    void Build()
    {
        SuspendLayout();
        foreach (Control old in Controls.Cast<Control>().ToList()) old.Dispose();
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
            Height = S(58),
            Margin = new Padding(S(8), S(4), S(8), 0),
            Font = new Font("Segoe UI", 9.5f),
            UseMnemonic = false,
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
        if (_ctl.Channels.Any(c => !c.IsGpu && !c.MonitorOnly && c.Ext == null))
        {
            var wz = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
            wz.Note("Welcher Anschluss ist was? Der Assistent hält jeden Anschluss kurz an und fragt, was stehen geblieben ist.");
            wz.Buttons(("Lüfter-Assistent starten", () => _ = RunWizard()));
            AddFull(wz);
            if (!_ctl.Settings.WizardOffered)
            {
                _ctl.Settings.WizardOffered = true;
                _ctl.Save();
                BeginInvoke(() =>
                {
                    if (MessageBox.Show(FindForm(), "Neu: Der Lüfter-Assistent ordnet in wenigen Minuten jeden Anschluss richtig zu " +
                            "(CPU-Kühler, Gehäuse-Lüfter, Pumpe). Jetzt starten?\n\n(Geht auch später im Reiter Lüfter.)",
                            "Lüfter-Assistent", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        _ = RunWizard();
                });
            }
        }
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
            // Gruppen zusammen anzeigen: erst die Gruppen (in Mitglieder-Reihenfolge), dann der Rest
            var ordered = _ctl.Settings.Groups.SelectMany(g => _ctl.MembersOf(g)).ToList();
            ordered.AddRange(_ctl.Channels.Where(c => !ordered.Contains(c)));
            FanCard? lastCard = null;
            foreach (var ch in ordered)
            {
                var card = new FanCard(ch, _ctl, () => BeginInvoke(Rebuild));
                _cards.Add(card);
                Controls.Add(card);
                lastCard = card;
            }
            if (lastCard != null) SetFlowBreak(lastCard, true);
        }

        // ----- Laptop (Tongfang) -----
        _laptopState = null;
        if (_ctl.Laptop is TongfangLaptop lap)
        {
            AddSection("Laptop-Lüfter", Color.FromArgb(148, 163, 184));
            var c = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
            _laptopState = c.Note(LaptopText(lap), muted: false);
            c.Note("Die Laptop-Lüfter lassen sich nur über den Leistungsmodus der Firmware steuern (wie im Control Center), nicht mit einer eigenen Kurve. " +
                   "Toast Temperatur stellt ihn passend zum Lüfterprofil ein: Energiesparend → Leise, Ausgewogen → Ausgewogen, Leistung → Leistung, " +
                   "Maximale Kühlung → Lüfter-Boost. In den Smart-Profilen geht es eine Stufe hoch, wenn CPU oder GPU deutlich über dem Ziel liegen. " +
                   "Beim Beenden wird der ursprüngliche Modus wiederhergestellt.");
            c.Check("Leistungsmodus des Laptops mit dem Lüfterprofil steuern", _ctl.Settings.LaptopControl,
                v => { _ctl.Settings.LaptopControl = v; _ctl.Save(); });
            AddFull(c);
        }

        // ----- Laptop (Uniwill-EC) -----
        if (_ctl.Uniwill is UniwillEc uw)
        {
            AddSection("Laptop-Lüfter", Color.FromArgb(148, 163, 184));
            var c = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
            c.Note("Die beiden Laptop-Lüfter werden direkt über den Embedded Controller gesteuert – über dieselbe Schnittstelle wie das Control Center " +
                   "(und wie der TUXEDO-Treiber unter Linux). Sie stehen oben als eigene Karten und regeln wie alle anderen Lüfter. " +
                   "Unter 12 % stehen sie still, zwischen 12 und 25 % laufen sie mit 25 % (darunter laufen sie nicht sicher an). " +
                   "Im Profil „Standard (BIOS)“ und beim Beenden übernimmt wieder der Laptop bzw. das Control Center." +
                   (uw.Board.Length > 0 ? $"  ·  Mainboard: {uw.Board}" : ""));
            c.Check("Laptop-Lüfter von Toast Temperatur steuern (nach dem Ausschalten: Neustart)", _ctl.Settings.LaptopControl,
                v => { _ctl.Settings.LaptopControl = v; _ctl.Save(); });
            c.Buttons(("Protokoll öffnen", () =>
            {
                var p = Path.Combine(AppPaths.Config, "laptop.log");
                try { if (File.Exists(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
            }));
            AddFull(c);
        }

        // ----- XMG Oasis -----
        _oasisState = null;
        if (OasisLink.Instance is OasisLink link)
        {
            AddSection("XMG Oasis (Bluetooth)", Color.FromArgb(56, 189, 248));
            AddFull(BuildOasisPanel(link));
        }

        // ----- Lautlos im Leerlauf -----
        if (_ctl.Calibratable.Any())
        {
            AddSection("Lautlos im Leerlauf", Color.FromArgb(129, 140, 248));
            AddFull(BuildZeroPanel());
        }


        // ----- Erweitert (zugeklappt): leere Anschlüsse, Gruppen, Kalibrierung, Sicherheit -----
        const string advText = "Erweiterte Einstellungen (leere Anschlüsse, Gruppen, Kalibrierung, Sicherheit)";
        var fold = Fold.Header(advText, _ctl.Settings.ShowAdvanced, K);
        AddFull(fold);
        int advStart = Controls.Count;
        // ----- Leere / ausgeblendete Anschlüsse -----
        List<FanController.HiddenPort> hidden;
        lock (_ctl.HiddenPorts) hidden = _ctl.HiddenPorts.ToList();
        if (hidden.Count > 0)
        {
            AddSection("Leere Anschlüsse", Color.FromArgb(148, 163, 184));
            var hc = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
            hc.Note($"Ausgeblendet, weil beim Start keine Drehzahl kam: {string.Join(", ", hidden.Select(h => h.Name))}. " +
                    "Fehlt ein Lüfter (z. B. die Radiator-Lüfter am CPU-Anschluss)? „Prüfen“ dreht alle leeren Anschlüsse kurz hoch " +
                    "und zeigt die, an denen sich etwas dreht. Lüfter ohne Drehzahlsignal kannst du auch direkt einblenden.");
            Button? probeBtn = null;
            hc.Buttons(("Leere Anschlüsse prüfen", async () =>
            {
                if (probeBtn != null) { probeBtn.Enabled = false; probeBtn.Text = "Prüfe … (7 s)"; }
                var found = await _ctl.ProbeHiddenAsync();
                MessageBox.Show(FindForm(), found.Count > 0
                        ? "Gefunden und eingeblendet: " + string.Join(", ", found.Select(f => f.Name)) + "\n\nMit „Erkennen“ auf der neuen Karte siehst du, welcher Lüfter es ist."
                        : "An keinem leeren Anschluss hat sich etwas gedreht (oder die Lüfter melden keine Drehzahl). Du kannst einen Anschluss trotzdem einblenden.",
                    "Leere Anschlüsse", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Rebuild();
            }));
            probeBtn = hc.Controls.OfType<Button>().LastOrDefault();
            foreach (var h in hidden)
            {
                var hh = h;
                hc.Buttons(($"{h.Name} einblenden", () => { _ctl.ShowPort(hh); Rebuild(); }));
            }
            AddFull(hc);
        }

        // ----- Gruppen -----
        if (_ctl.Groupable.Count() >= 2)
        {
            AddSection("Lüftergruppen", Color.FromArgb(167, 139, 250));
            AddFull(new GroupsPanel(_ctl, () => BeginInvoke(Rebuild)) { Margin = new Padding(S(6), S(2), S(6), S(4)) });
        }

        // ----- Kalibrierung -----
        _calib = null;
        if (_ctl.Calibratable.Any())
        {
            AddSection("Kalibrierung", Color.FromArgb(45, 212, 191));
            _calib = new CalibrationPanel(_ctl, () => BeginInvoke(Rebuild)) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
            AddFull(_calib);

        }
        // ----- Sicherheit (optional) -----
        AddSection("Sicherheit (optional)", Theme.Hot);
        AddFull(BuildSafetyPanel());

        Fold.Bind(fold, advText, Controls.Cast<Control>().Skip(advStart).ToList(), _ctl.Settings.ShowAdvanced,
            v => { _ctl.Settings.ShowAdvanced = v; _ctl.Save(); });

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

    Control BuildOasisPanel(OasisLink link)
    {
        var st = _ctl.Settings;
        var c = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        _oasisState = c.Note(OasisText(link), muted: false);
        c.Note("Lüfter und Pumpe der Oasis werden wie alle anderen geregelt (Profile, Smart-Automatik, Gruppen) – nach CPU und GPU des Laptops. " +
               "Bitte im XMG Control Center die Wasserkühlung nicht gleichzeitig steuern, sonst überschreiben sich beide.");
        c.Combo("Pumpenspannung:", new[] { "11 V", "12 V (stärkste)", "7 V (leiseste)", "8 V" }, st.OasisPumpVoltage,
            i => { st.OasisPumpVoltage = i; _ctl.Save(); });
        c.Numbers(
            ("Wenn Toast Temperatur nicht steuert: Lüfter", st.OasisReleaseFan, 0, 100, "%", (Action<float>)(v => { st.OasisReleaseFan = v; _ctl.Save(); })),
            ("Pumpe", st.OasisReleasePump, 20, 100, "%", (Action<float>)(v => { st.OasisReleasePump = v; _ctl.Save(); })));
        c.Buttons(
            ("Neu suchen", () =>
            {
                link.Forget();
                _ctl.Save();
            }),
            ("Protokoll öffnen", () =>
            {
                var p = Path.Combine(AppPaths.Config, "oasis.log");
                try { if (File.Exists(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
            }));
        c.Check("Nach der XMG Oasis suchen (nach dem Ausschalten: Neustart)", st.OasisEnabled, v => { st.OasisEnabled = v; _ctl.Save(); });
        return c;
    }

    string LaptopText(TongfangLaptop l) =>
        $"CPU-Lüfter {Rpm(l.CpuRpm)}  ·  GPU-Lüfter {Rpm(l.GpuRpm)}" + (l.SysRpm is > 0 ? $"  ·  System-Lüfter {Rpm(l.SysRpm)}" : "") +
        $"\n{_ctl.LaptopStatus}";

    static string Rpm(int? r) => r is int v ? $"{v:N0} U/min" : "–";

    static string OasisText(OasisLink l)
    {
        if (!l.Connected) return "● " + l.State;
        string flow = l.Flow switch { 2 => "Durchfluss in Ordnung", -1 => "Durchfluss: warte auf Meldung", _ => $"⚠ Durchfluss gestört (Code {l.Flow})" };
        return $"● Verbunden · {l.Model ?? "XMG Oasis"}" + (l.Firmware != null ? $" · Firmware {l.Firmware}" : "") + $" · {flow}" +
               (l.ControlCenterRunning ? "  ·  Control Center läuft ebenfalls" : "");
    }

    Control BuildZeroPanel()
    {
        var st = _ctl.Settings;
        var c = new SettingsCard(K) { Margin = new Padding(S(6), S(2), S(6), S(4)) };
        c.Note("Im Leerlauf bleiben die Lüfter ganz stehen – wie bei Grafikkarten. Sobald es wärmer wird, laufen sie mit einem kurzen " +
               "Anschub wieder an. Gilt in den Profilen „Energiesparend“ und „Ausgewogen“, nie für Pumpen. Jeder Lüfter achtet auf die " +
               "Bauteile, die bei ihm angehakt sind.");
        c.Check("Lüfter im Leerlauf anhalten", st.ZeroRpmEnabled, v => { st.ZeroRpmEnabled = v; _ctl.Save(); });
        c.Numbers(
            ("Stehen bleiben, solange CPU unter", st.ZeroCpu, 30, 75, "°C", (Action<float>)(v => { st.ZeroCpu = v; _ctl.Save(); })),
            ("und GPU unter", st.ZeroGpu, 30, 75, "°C", (Action<float>)(v => { st.ZeroGpu = v; _ctl.Save(); })));
        c.Note("Wieder anlaufen: 4 °C darüber. Mainboard-Lüfter ohne CPU/GPU-Haken bleiben stehen, solange das Mainboard 10 °C unter seiner Grenze ist.");

        var cand = _ctl.StopCandidates.ToList();
        var missing = _ctl.Channels.Where(x => !x.IsGpu && x.Kind == FanKind.Fan && !x.NotControllable && x.Cfg.StartPercent == null).ToList();
        if (cand.Count > 0)
        {
            c.Space(4);
            c.Note("Diese Lüfter dürfen stehen bleiben:", muted: false);
            foreach (var ch in cand)
            {
                var fan = ch;
                c.Check(fan.DisplayName + (fan.Cfg.StartPercent is float sp ? $"   (läuft ab {sp:0} % an)" : ""), fan.Cfg.AllowStop,
                    v => { fan.Cfg.AllowStop = v; _ctl.Save(); });
            }
        }
        if (missing.Count > 0)
            c.Note($"Nicht möglich für: {string.Join(", ", missing.Select(m => m.DisplayName))} – noch nicht kalibriert oder " +
                   "der Lüfter bleibt auch bei 0 % nicht stehen. Erst oben kalibrieren.");
        return c;
    }

    Control BuildSafetyPanel()
    {
        var s = _ctl.Settings;
        var panel = new Panel
        {
            Height = S(156),
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
        foreach (var c in Components.All.Where(x => x != Component.Hotspot || _ctl.HasComponent(Component.Hotspot)))
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
                else if (comp == Component.Hotspot) s.HotspotCritical = v;
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

        // Mainboard-Grenze (standardmäßig an)
        var guard = new CheckBox
        {
            Text = "Mainboard-Grenze: Mainboard-Lüfter hochregeln, wenn das Mainboard wärmer wird als",
            Checked = s.BoardGuardEnabled,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(S(14), S(116)),
        };
        int gx = S(14) + TextRenderer.MeasureText(guard.Text, guard.Font).Width + S(30);
        var guardNum = Ui.Number(s.BoardLimit, 40, 100, new Point(gx, S(113)), K);
        var guardUnit = new Label { Text = "°C", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(gx + guardNum.Width + S(4), S(116)) };
        guardNum.Enabled = guard.Checked;
        guard.CheckedChanged += (_, _) => { s.BoardGuardEnabled = guard.Checked; guardNum.Enabled = guard.Checked; _ctl.Save(); };
        guardNum.ValueChanged += (_, _) => { s.BoardLimit = (float)guardNum.Value; _ctl.Save(); };
        controls.AddRange(new Control[] { guard, guardNum, guardUnit });
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
        int w = DarkMode.FullWidth(this, S(16));
        foreach (var c in _fullWidth) c.Width = w;
    }

    public void SelectProfile(FanProfile p)
    {
        if (p == _ctl.Profile && _ctl.Override == null) return;

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
        SyncProfile();
        ProfileChanged?.Invoke();
    }

    /// Anzeige an das wirksame Profil anpassen (auch nach automatischem Wechsel)
    public void SyncProfile()
    {
        foreach (var b in _buttons) b.Selected = b.Info.Profile == _ctl.Profile;
        SuspendLayout();
        UpdateProfileInfo();
        ApplyCustomMode();
        ResumeLayout();
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
        if (_ctl.Override != null && _ctl.OverrideReason != null)
            _profileInfo.Text = $"⟳ Automatisch gewechselt ({_ctl.OverrideReason}). Deine Wahl: „{Profiles.Get(_ctl.ManualProfile).Title}“ – " +
                                "ein Klick auf ein Profil übernimmt wieder von Hand.\n" + _profileInfo.Text;
        _targets?.ShowFor(p);
    }

    public void RefreshValues()
    {
        foreach (var c in _cards) c.RefreshValues();
        _calib?.RefreshValues();
        if (_laptopState != null && _ctl.Laptop is TongfangLaptop lp)
        {
            string t = LaptopText(lp);
            if (_laptopState.Text != t) _laptopState.Text = t;
        }
        if (_oasisState != null && OasisLink.Instance is OasisLink ol)
        {
            string t = OasisText(ol);
            if (_oasisState.Text != t)
            {
                _oasisState.Text = t;
                _oasisState.ForeColor = !ol.Connected ? Theme.Warm : ol.Flow is >= 0 and not 2 ? Theme.Hot : Theme.Cool;
            }
        }
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
        float k = Dpi.K(this);
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
        foreach (var c in Components.All.Where(x => x != Component.Hotspot || ctl.HasComponent(Component.Hotspot)))
        {
            var lbl = new Label { Text = $"Ziel {Components.Name(c)}", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, S(45)) };
            x += TextRenderer.MeasureText(lbl.Text, lbl.Font).Width + S(6);
            var num = Ui.Number(60, 30, 110, new Point(x, S(42)), k);
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
        foreach (var c in _nums.Keys)
        {
            _nums[c].Value = (decimal)Math.Clamp(t[(int)c], 30, 110);
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
    Font TitleFont => Dpi.F("Segoe UI Semibold", 10.5f, this);
    Font DescFont => Dpi.F("Segoe UI", 8.5f, this);
    Font TagFont => Dpi.F("Segoe UI Semibold", 7f, this);

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
        float k = Dpi.K(this);
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
        float k = Dpi.K(this);
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
    Font NameFont => Dpi.F("Segoe UI Semibold", 11f, this);
    Font SmallFont => Dpi.F("Segoe UI", 8.5f, this);
    Font BigFont => Dpi.F("Segoe UI Semibold", 20f, this);

    readonly FanChannel _ch;
    readonly FanController _ctl;
    readonly TextBox _name;
    readonly ComboBox _kind;
    readonly Button _identify;
    readonly Button? _hide;
    readonly LinkLabel _reset;
    readonly Dictionary<Component, CheckBox> _sources = new();
    readonly Label _sourceLabel;
    readonly CurveEditor _curve;
    readonly ComboBox? _group;
    readonly Label _follow;
    readonly Action _rebuild;
    readonly bool _follower;
    bool _showCurve;

    float K => Dpi.K(this);
    int S(float px) => (int)(px * K);

    const int BaseHeight = 236;

    public FanCard(FanChannel ch, FanController ctl, Action rebuild)
    {
        _ch = ch;
        _ctl = ctl;
        _rebuild = rebuild;
        _follower = ctl.IsFollower(ch);
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
        new ToolTip().SetToolTip(_identify, "Lässt nur diesen Lüfter kurz auf 100 % laufen, damit du ihn findest – und prüft dabei, ob er sich steuern lässt");
        _identify.Click += (_, _) =>
        {
            // Schutz geht vor: Pumpe oder CPU-Kühler nur nach Bestätigung anhalten, bei Wärme gar nicht
            bool critical = ch.Kind == FanKind.Pump || (ch.Cfg.UseCpu && !ch.Cfg.UseGpu && !ch.IsGpu);
            if (!ctl.SafeToStop(out var why))
            {
                MessageBox.Show(FindForm(), $"Zum Erkennen wird nicht angehalten, weil gerade {why} ist.\n" +
                    "Der Lüfter dreht stattdessen kurz voll hoch.", "Erkennen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (critical && MessageBox.Show(FindForm(),
                         $"„{ch.DisplayName}“ wird zum Erkennen kurz angehalten ({(ch.Kind == FanKind.Pump ? 5 : 7)} Sekunden).\n\n" +
                         "Toast Temperatur bricht sofort ab und kühlt voll, sobald die Temperatur steigt. " +
                         "Trotzdem gilt: Benutzung auf eigene Gefahr.\n\nJetzt anhalten?",
                         "Erkennen – bitte bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            ctl.Identify(ch);
        };

        _reset = new LinkLabel
        {
            Text = "Gelerntes zurücksetzen",
            LinkColor = Theme.Muted,
            ActiveLinkColor = Theme.Text,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Theme.Card,
            AutoSize = true,
        };
        new ToolTip().SetToolTip(_reset, "Vergisst, was die Smart-Automatik für diesen Lüfter gelernt hat, und lernt neu");
        _reset.Click += (_, _) => ctl.ResetLearning(ch);

        _sourceLabel = new Label
        {
            Text = "Regelt nach:",
            ForeColor = Theme.Muted,
            BackColor = Theme.Card,
            AutoSize = true,
        };

        foreach (var c in Components.Selectable)
        {
            var cb = new CheckBox
            {
                Checked = ch.Cfg.Uses(c),
                Enabled = ctl.HasComponent(c),
                Visible = !ch.IsGpu,   // GPU-Lüfter regeln fest nach der GPU – keine Auswahl nötig
                ForeColor = Theme.Text,
                BackColor = Theme.Card,
                AutoSize = false,
            };
            var comp = c;
            cb.CheckedChanged += (_, _) =>
            {
                ch.Cfg.SetUse(comp, cb.Checked);
                ch.Cfg.SourcesUserSet = true;
                ctl.Save();
            };
            _sources[c] = cb;
        }

        _curve = new CurveEditor(ch.Cfg.Curve) { Visible = false };
        _curve.CurveChanged += () => ctl.Save();

        // Gruppe wählen (nicht für Grafikkarten-Lüfter)
        _follow = new Label { ForeColor = Theme.Muted, BackColor = Theme.Card, AutoSize = false, UseMnemonic = false, Visible = false };
        if (!ch.IsGpu && ctl.Groupable.Count() >= 2)
        {
            _group = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = Theme.CardHero, ForeColor = Theme.Text,
            };
            var groups = ctl.Settings.Groups.ToList();
            _group.Items.Add("Keine Gruppe");
            foreach (var g in groups) _group.Items.Add("Gruppe: " + g.Name);
            _group.Items.Add("Neue Gruppe …");
            var mine = ctl.GroupOf(ch);
            _group.SelectedIndex = mine == null ? 0 : groups.IndexOf(mine) + 1;
            int initial = _group.SelectedIndex;
            _group.SelectedIndexChanged += (_, _) =>
            {
                int i = _group.SelectedIndex;
                if (i == initial) return;
                FanGroup? target = null;
                if (i == _group.Items.Count - 1)
                {
                    var name = Prompt.Ask(FindForm(), "Neue Gruppe", "Name der Gruppe, z. B. „Front“ oder „Radiator“:", "");
                    if (name == null) { _group.SelectedIndex = initial; return; }
                    target = ctl.NewGroup(name);
                }
                else if (i > 0) target = groups[i - 1];
                ctl.SetGroup(ch, target);
                _rebuild();
            };
            new ToolTip().SetToolTip(_group, "Lüfter einer Gruppe drehen gemeinsam: der erste regelt, die anderen übernehmen seine Leistung");
            Controls.Add(_group);

            if (_follower && ctl.GroupOf(ch) is FanGroup fg && ctl.LeaderOf(fg) is FanChannel lead)
            {
                _follow.Text = $"Folgt „{lead.DisplayName}“ – Temperaturquellen und Kurve kommen vom führenden Lüfter der Gruppe.";
                _follow.Visible = true;
                foreach (var cb in _sources.Values) cb.Visible = false;
            }
        }
        Controls.Add(_follow);

        Controls.Add(_name);
        Controls.Add(_kind);
        Controls.Add(_identify);
        if (ch.ManualPort != null)
        {
            _hide = new Button { Text = "Entfernen", FlatStyle = FlatStyle.Flat, BackColor = Theme.CardHero, ForeColor = Theme.Muted, Cursor = Cursors.Hand };
            _hide.FlatAppearance.BorderColor = Theme.Border;
            new ToolTip().SetToolTip(_hide, "Diesen von Hand eingeblendeten Anschluss wieder ausblenden – die Steuerung geht ans Mainboard (BIOS) zurück");
            _hide.Click += (_, _) =>
            {
                if (MessageBox.Show(FindForm(), $"„{ch.DisplayName}“ wieder ausblenden?\n\nDer Anschluss wird dann wieder vom Mainboard (BIOS) geregelt. " +
                        "Unter „Leere Anschlüsse“ kannst du ihn jederzeit wieder einblenden.", "Lüfter entfernen",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ctl.HidePort(ch);
                _rebuild();
            };
            Controls.Add(_hide);
        }
        Controls.Add(_reset);
        Controls.Add(_sourceLabel);
        foreach (var cb in _sources.Values) Controls.Add(cb);
        Controls.Add(_curve);
        UpdateSourceTexts();
    }

    /// Nach „Erkennen“: Was hat sich bewegt? -> Name, Art und Temperaturquellen passend setzen
    void AskIdentified()
    {
        if (AskRole(_ch, _ctl, FindForm()!, null) == true) _rebuild();
    }

    /// Nach „Erkennen“: Was hat sich bewegt? -> Name, Art und Temperaturquellen passend setzen.
    /// true = gesetzt, false = übersprungen, null = Assistent abbrechen
    public static bool? AskRole(FanChannel ch, FanController ctl, IWin32Window owner, string? step)
    {
        var bCpu = new TaskDialogButton("CPU-Kühler / Radiator-Lüfter");
        var bCase = new TaskDialogButton("Gehäuse-Lüfter");
        var bPump = new TaskDialogButton("Pumpe (Wasserkühlung)");
        var bNone = new TaskDialogButton(step != null ? "Nichts gesehen – überspringen" : "Nichts gesehen / weiß nicht");
        var bStop = new TaskDialogButton("Assistent beenden");
        var page = new TaskDialogPage
        {
            Caption = step != null ? "Lüfter-Assistent · " + step : "Erkennen",
            Heading = $"„{ch.DisplayName}“ – was ist eben stehen geblieben bzw. hochgedreht?",
            Text = (ch.IdentifyResult ?? "") + "\n\nDeine Auswahl setzt Namen, Art und Temperaturquellen passend (später jederzeit änderbar).",
            Icon = TaskDialogIcon.Information,
            AllowCancel = true,
        };
        foreach (var b in new[] { bCpu, bCase, bPump, bNone }) page.Buttons.Add(b);
        if (step != null) page.Buttons.Add(bStop);
        var r = TaskDialog.ShowDialog(owner, page);
        if (r == bStop || (step != null && r == TaskDialogButton.Cancel)) return null;
        var cfg = ch.Cfg;
        if (r == bCpu)
        {
            cfg.Kind = FanKind.Fan;
            cfg.CustomName = "CPU-Kühler";
            cfg.UseCpu = true; cfg.UseGpu = false; cfg.UseBoard = false;
        }
        else if (r == bCase)
        {
            cfg.Kind = FanKind.Fan;
            cfg.CustomName = "Gehäuse-Lüfter";
            cfg.UseCpu = true; cfg.UseGpu = true; cfg.UseBoard = false;
        }
        else if (r == bPump)
        {
            cfg.Kind = FanKind.Pump;
            cfg.CustomName = "Pumpe";
            cfg.UseCpu = true; cfg.UseGpu = false; cfg.UseBoard = false;
        }
        else return false;
        cfg.SourcesUserSet = true;
        cfg.SourcesInitialized = true;
        ctl.Save();
        return true;
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
        _showCurve = show && !_follower;
        ApplyLayout();
    }

    void ApplyLayout()
    {
        Size = new Size(S(420), _showCurve ? S(BaseHeight + 186) : S(BaseHeight));
        _name.SetBounds(S(48), S(14), Width - S(210), S(24));
        _kind.SetBounds(S(16), S(62), S(96), S(26));
        _identify.SetBounds(S(120), S(61), S(100), S(28));
        if (_hide != null)
        {
            // Platz für „Entfernen“: Link kürzer und weiter rechts
            _hide.SetBounds(S(228), S(61), S(86), S(28));
            _reset.Text = "Zurücksetzen";
            _reset.Location = new Point(S(322), S(67));
        }
        if (_hide == null) _reset.Location = new Point(S(232), S(67));
        _sourceLabel.Location = new Point(S(16), _ch.IsGpu ? S(160) : S(150));
        _group?.SetBounds(Width - S(196), S(144), S(180), S(26));
        _follow.SetBounds(S(16), S(170), Width - S(32), S(26));
        if (_follower) _sourceLabel.Text = "Gruppe:";
        int x = S(16), w = (Width - S(32)) / 3;
        foreach (var c in Components.Selectable)
        {
            _sources[c].SetBounds(x, S(168), w, S(24));
            x += w;
        }
        _curve.Visible = _showCurve && !_follower;
        _curve.SetBounds(S(12), S(BaseHeight + 4), Width - S(24), S(176));
        Invalidate();
    }

    void UpdateSourceTexts()
    {
        if (_follower) return;
        if (_ch.IsGpu)
        {
            string txt;
            if (_ctl.GpuHotspot is float hs)
            {
                txt = $"Regelt nach dem GPU-Hot-Spot:  {hs:0} °C";
                if (_ctl.Temp(Component.Gpu) is float gt) txt += $"  ·  GPU {gt:0} °C";
            }
            else
            {
                txt = "Regelt nach der Grafikkarte";
                if (_ctl.Temp(Component.Gpu) is float gt) txt += $":  {gt:0} °C";
            }
            if (_ctl.Load(Component.Gpu) is float gl) txt += $"  ·  Last {gl:0} %";
            if (_sourceLabel.Text != txt) _sourceLabel.Text = txt;
            return;
        }
        foreach (var c in Components.Selectable)
        {
            string txt = Components.Name(c);
            if (!_ctl.HasComponent(c)) txt += " (fehlt)";
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
        if (_ch.IdentifyFinished && !_ch.InWizard)
        {
            _ch.IdentifyFinished = false;
            BeginInvoke(AskIdentified);
        }
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

        string sub = (pump ? "Pumpe" : "Lüfter") + " · " + _ch.HardwareName + (_ch.Od8 != null ? " · AMD Overdrive 8" : _ch.Od8Note != null ? " · Overdrive 8 nicht verfügbar" : "") +
                     (_ch.DisplayName != _ch.DefaultName ? $" · {_ch.DefaultName}" : "") +
                     (_ctl.GroupOf(_ch) is FanGroup grp ? $" · Gruppe „{grp.Name}“" : "") +
                     (_ch.Cfg.StartPercent is float sp && !pump ? $" · startet ab {sp:0} %" : "") +
                     (_ch.Cfg.MaxRpm is float mx && mx > 0 ? $" · max {mx:N0} U/min" : "");
        TextRenderer.DrawText(g, sub, SmallFont, new Rectangle(S(48), S(40), Width - S(210), S(16)), Theme.Muted, L);

        // Drehzahl groß
        // Ohne Drehzahl-Sensor (Oasis, Laptop-Lüfter): Leistung groß anzeigen
        float shown = _ch.Output >= 0 ? _ch.Output : (_ch.Readback ?? -1);
        string big = _ch.Rpm != null ? (_ch.Rpm.Value is float r ? $"{r:N0}" : "–") : (shown >= 0 ? $"{shown:0}" : "–");
        string unit = _ch.Rpm != null ? "U/min" : "%";
        TextRenderer.DrawText(g, big, BigFont, new Rectangle(Width - S(160), S(10), S(110), S(36)), Theme.Text, R);
        TextRenderer.DrawText(g, unit, SmallFont, new Rectangle(Width - S(48), S(22), S(36), S(16)), Theme.Muted, L);

        // Leistungsbalken
        float pct = _ch.Output >= 0 ? _ch.Output : (_ch.Readback ?? 0);
        bool bios = _ctl.Profile == FanProfile.Bios || _ch.Faulted || _ch.NotControllable || _ch.Output < 0;
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
                      : _ch.NotControllable ? Theme.Warm
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
    Font SmallFont => Dpi.F("Segoe UI", 8f, this);

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

    float K => Dpi.K(this);

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

// ===========================================================================
// Lüftergruppen: automatisch erkennen oder von Hand zusammenstellen
// ===========================================================================
sealed class GroupsPanel : Panel
{
    readonly FanController _ctl;
    readonly Action _changed;
    float K => Dpi.K(this);
    int S(float px) => (int)(px * K);

    public GroupsPanel(FanController ctl, Action changed)
    {
        _ctl = ctl;
        _changed = changed;
        BackColor = Theme.Card;
        Height = 100;
    }

    int _builtWidth = -1;

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Build();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated && Width != _builtWidth) Build();
    }

    Label Lbl(string t, int x, int y, Color col, int w = 0)
    {
        var l = new Label
        {
            Text = t, ForeColor = col, Location = new Point(x, y), UseMnemonic = false,
            AutoSize = w == 0, AutoEllipsis = w > 0,
        };
        if (w > 0) l.Size = new Size(w, S(22));
        Controls.Add(l);
        return l;
    }

    void Build()
    {
        SuspendLayout();
        _builtWidth = Width;
        foreach (Control old in Controls.Cast<Control>().ToList()) { Controls.Remove(old); old.Dispose(); }
        int y = S(12);
        var note = Lbl("Lüfter einer Gruppe drehen immer gemeinsam: Der führende Lüfter regelt nach seinen Temperaturquellen bzw. seiner Kurve, " +
                       "die anderen übernehmen seine Leistung (Pumpen behalten ihre Mindestleistung). Zuordnen über „Gruppe“ auf jeder Lüfter-Karte " +
                       "oder automatisch erkennen lassen.", S(14), y, Theme.Muted);
        note.MaximumSize = new Size(Math.Max(S(300), Width - S(28)), 0);
        y += note.PreferredHeight + S(10);

        var groups = _ctl.Settings.Groups.ToList();
        if (groups.Count == 0)
        {
            Lbl("Noch keine Gruppen.", S(14), y, Theme.Text);
            y += S(30);
        }
        foreach (var g in groups)
        {
            var members = _ctl.MembersOf(g).ToList();
            int x = S(14);

            var name = new TextBox
            {
                Text = g.Name, BackColor = Theme.CardHero, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(x, y), Width = S(150),
            };
            void Rename()
            {
                var n = name.Text.Trim();
                if (n.Length == 0 || n == g.Name) { name.Text = g.Name; return; }
                g.Name = n;
                _ctl.Save();
                _changed();
            }
            name.Leave += (_, _) => Rename();
            name.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Rename(); } };
            Controls.Add(name);
            x += S(162);

            Lbl("führt:", x, y + S(3), Theme.Muted);
            x += S(42);
            var lead = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = Theme.CardHero, ForeColor = Theme.Text, Location = new Point(x, y - S(1)), Width = S(170),
            };
            foreach (var m in members) lead.Items.Add(m.DisplayName);
            var leader = _ctl.LeaderOf(g);
            if (members.Count > 0) lead.SelectedIndex = Math.Max(0, leader == null ? 0 : members.IndexOf(leader));
            int initial = lead.SelectedIndex;
            lead.SelectedIndexChanged += (_, _) =>
            {
                if (lead.SelectedIndex == initial || lead.SelectedIndex < 0) return;
                _ctl.SetLeader(g, members[lead.SelectedIndex]);
                _changed();
            };
            Controls.Add(lead);
            x += S(182);

            string others = members.Count <= 1
                ? "nur ein Lüfter – weitere über „Gruppe“ auf der Karte hinzufügen"
                : $"{members.Count} Lüfter: " + string.Join(", ", members.Select(m => m.DisplayName));
            var info = Lbl(others, x, y + S(3), Theme.Text, Math.Max(S(80), Width - x - S(110)));
            info.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

            var del = new LinkLabel
            {
                Text = "Auflösen", LinkColor = Theme.Muted, ActiveLinkColor = Theme.Text, AutoSize = true,
                LinkBehavior = LinkBehavior.HoverUnderline, Location = new Point(Width - S(84), y + S(3)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            del.Click += (_, _) => { _ctl.RemoveGroup(g); _changed(); };
            Controls.Add(del);
            y += S(34);
        }

        y += S(4);
        int bx = S(14);
        Button Btn(string text, int w, Action click)
        {
            var b = PageBar.Button(text, K, w);
            b.Location = new Point(bx, y);
            b.Height = S(30);
            b.Click += (_, _) => click();
            Controls.Add(b);
            bx += b.Width + S(10);
            return b;
        }
        Btn("Automatisch gruppieren", 190, AutoGroup);
        Btn("Neue Gruppe …", 140, () =>
        {
            var n = Prompt.Ask(FindForm(), "Neue Gruppe", "Name der Gruppe, z. B. „Front“ oder „Radiator“:", "");
            if (n == null) return;
            _ctl.NewGroup(n);
            _ctl.Save();
            _changed();
        });
        if (groups.Count > 0)
            Btn("Alle auflösen", 130, () => { _ctl.ReplaceGroups(new()); _changed(); });
        y += S(44);
        Height = y;
        ResumeLayout();
    }

    void AutoGroup()
    {
        var sug = _ctl.SuggestGroups();
        if (sug.Count == 0)
        {
            MessageBox.Show(FindForm(),
                "Keine passenden Lüfter gefunden.\n\nTipp: Benenne zusammengehörige Lüfter gleich, z. B. „Front 1“, „Front 2“, „Front 3“ – " +
                "dann werden sie erkannt. Oder stelle die Gruppen über „Gruppe“ auf den Lüfter-Karten selbst zusammen.",
                "Automatisch gruppieren", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var text = string.Join("\n", sug.Select(g =>
            $"•  {g.Name}: " + string.Join(", ", g.Members.Select(id => _ctl.ById(id)?.DisplayName ?? id))));
        string replace = _ctl.Settings.Groups.Count > 0 ? "\n\nDie bisherigen Gruppen werden ersetzt." : "";
        if (MessageBox.Show(FindForm(),
                $"Vorschlag (nach Namen, Pumpen und gleichen Temperaturquellen):\n\n{text}\n\nDer jeweils erste Lüfter führt die Gruppe.{replace}\n\nSo übernehmen?",
                "Automatisch gruppieren", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _ctl.ReplaceGroups(sug);
        _changed();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

// ===========================================================================
// Kalibrierung: Starthilfe-Grenze und Höchstdrehzahl je Lüfter messen
// ===========================================================================
sealed class CalibrationPanel : Panel
{
    readonly FanController _ctl;
    readonly Label _note, _state;
    readonly Button _btn;
    float K => Dpi.K(this);
    int S(float px) => (int)(px * K);

    readonly Action _finished;
    bool _wasRunning;

    public CalibrationPanel(FanController ctl, Action finished)
    {
        _ctl = ctl;
        _finished = finished;
        BackColor = Theme.Card;
        _note = new Label
        {
            ForeColor = Theme.Muted, AutoSize = true, UseMnemonic = false,
            Text = "Misst für jeden Mainboard-Lüfter die höchste Drehzahl und ab wie viel Prozent er sicher anläuft. " +
                   "Diese Grenze wird danach als Untergrenze genutzt – so bleibt kein Lüfter bei niedriger Leistung stehen. " +
                   "Dauert etwa 1–2 Minuten, am besten im Leerlauf. Pumpen werden nur bei 100 % gemessen und nie angehalten.",
        };
        _btn = PageBar.Button("Alle Lüfter kalibrieren", K, 210);
        _btn.Click += (_, _) => Toggle();
        _state = new Label { ForeColor = Theme.Text, AutoSize = true, UseMnemonic = false };
        Controls.AddRange(new Control[] { _note, _btn, _state });
        Resize += (_, _) => Layout2();
        RefreshValues();
    }

    void Layout2()
    {
        _note.Location = new Point(S(14), S(12));
        _note.MaximumSize = new Size(Math.Max(S(300), Width - S(28)), 0);
        int y = S(12) + _note.PreferredHeight + S(10);
        _btn.Location = new Point(S(14), y);
        _btn.Height = S(30);
        _state.Location = new Point(_btn.Right + S(14), y + S(6));
        _state.MaximumSize = new Size(Math.Max(S(200), Width - _state.Left - S(14)), 0);
        int h = Math.Max(_btn.Bottom, _state.Bottom) + S(12);
        if (Height != h) Height = h;
    }

    void Toggle()
    {
        if (_ctl.Calibrating)
        {
            _ctl.CancelCalibration();
            RefreshValues();
            return;
        }
        if (MessageBox.Show(FindForm(),
                "Die Lüfter laufen dabei nacheinander auf 100 %, kurz auf 0 % und dann langsam wieder hoch.\n\n" +
                "Bei hohen Temperaturen bricht die Messung von selbst ab.\nJetzt kalibrieren?",
                "Lüfter kalibrieren", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _ctl.StartCalibration();
        RefreshValues();
    }

    public void RefreshValues()
    {
        bool run = _ctl.Calibrating;
        if (_wasRunning && !run) { _wasRunning = false; _finished(); return; }
        _wasRunning = run;
        string bt = run ? "Kalibrierung abbrechen" : "Alle Lüfter kalibrieren";
        if (_btn.Text != bt) _btn.Text = bt;

        string st;
        if (run)
        {
            var list = _ctl.Calibratable.ToList();
            int done = list.Count(c => c.CalPhase == 0);
            st = $"Läuft … {done} von {list.Count} fertig";
        }
        else
        {
            var cal = _ctl.Calibratable.Where(c => c.Cfg.CalibratedAt != null).ToList();
            var notes = _ctl.Channels.Where(c => c.CalNote != null).Select(c => $"{c.DisplayName}: {c.CalNote}").ToList();
            st = notes.Count > 0 ? string.Join("\n", notes)
               : cal.Count > 0 ? $"{cal.Count} Lüfter kalibriert (zuletzt {cal.Max(c => c.Cfg.CalibratedAt):dd.MM.yyyy})"
               : "Noch nicht kalibriert.";
        }
        if (_state.Text != st) { _state.Text = st; Layout2(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
