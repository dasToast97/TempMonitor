using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TempMonitor;

// ===========================================================================
// Spiele erkennen: eigene Liste (Programmname) oder automatisch über den
// Installationsort (Steam, Epic, GOG, Xbox, EA, Ubisoft, Riot …)
// ===========================================================================
static class GameDetector
{
    static readonly string[] StorePaths =
    {
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\xboxgames\",
        @"\riot games\", @"\ubisoft game launcher\games\", @"\ea games\", @"\electronic arts\", @"\origin games\",
        @"\battle.net games\", @"\rockstar games\", @"\amazon games\library\",
    };

    /// Launcher, Hilfsprogramme und Dauerläufer, die kein Spiel sind
    static readonly string[] NotGames =
    {
        @"\epic games\launcher\", @"\riot client\", @"\ea desktop\", @"\rockstar games\launcher\",
        @"\steamworks shared\", "wallpaper_engine", "wallpaper32", "wallpaper64", "crashhandler", "crashreport",
        "crashpad", "easyanticheat", "battleye", "redist", "vc_redist", "dxsetup", "installer", "setup", "uninst",
        "launcher.exe", "unrealcefsubprocess", "cefsharp", "webhelper",
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    static string Exe(string s)
    {
        s = s.Trim().Trim('"');
        s = Path.GetFileName(s);
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : s + ".exe";
    }

    /// Liefert einen lesbaren Namen des laufenden Spiels oder null
    public static string? Find(IEnumerable<string> exes, IEnumerable<string> ignore, bool stores)
    {
        var mine = new HashSet<string>(exes.Where(e => !string.IsNullOrWhiteSpace(e)).Select(Exe), StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>(ignore.Where(e => !string.IsNullOrWhiteSpace(e)).Select(Exe), StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.Id <= 4) continue;
                    string exe = p.ProcessName + ".exe";
                    if (skip.Contains(exe)) continue;
                    if (mine.Contains(exe)) return p.ProcessName;
                    if (!stores) continue;

                    var path = ImagePath(p.Id);
                    if (path == null) continue;
                    string low = path.ToLowerInvariant();
                    if (!StorePaths.Any(low.Contains) || NotGames.Any(low.Contains)) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;   // Hintergrund-Helfer
                    return NiceName(path, p.ProcessName);
                }
                catch { /* Prozess beendet / kein Zugriff */ }
            }
        }
        return null;
    }

    /// Ordnername des Spiels statt "Win64-Shipping.exe"
    static string NiceName(string path, string fallback)
    {
        string low = path.ToLowerInvariant();
        foreach (var sp in StorePaths)
        {
            int i = low.IndexOf(sp, StringComparison.Ordinal);
            if (i < 0) continue;
            string rest = path[(i + sp.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash > 0) return rest[..slash];
        }
        return fallback;
    }

    /// Programme mit sichtbarem Fenster – zur Auswahl für die eigene Spiele-Liste
    public static List<(string Exe, string Title)> RunningWithWindow()
    {
        var list = new List<(string, string)>();
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    string exe = p.ProcessName + ".exe";
                    if (exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (list.Any(l => l.Item1.Equals(exe, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add((exe, p.MainWindowTitle));
                }
                catch { }
            }
        }
        return list.OrderBy(l => l.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

// ===========================================================================
// Automatischer Profilwechsel: Spiel geht vor Zeitplan, Zeitplan vor eigener Wahl.
// Wählt man selbst ein Profil, gilt das, bis der Anlass (Spiel / Zeitraum) endet.
// ===========================================================================
sealed class AutoSwitcher
{
    DateTime _lastScan = DateTime.MinValue;
    Task<string?>? _scan;
    string? _game;

    public string? CurrentGame => _game;

    /// Einmal pro Sekunde auf dem UI-Thread. Gibt eine Meldung zurück, wenn das Profil gewechselt hat.
    public string? Update(FanController ctl)
    {
        var s = ctl.Settings;
        var now = DateTime.Now;

        if (_scan is { IsCompleted: true })
        {
            _game = _scan.Status == TaskStatus.RanToCompletion ? _scan.Result : null;
            _scan = null;
        }
        if (!s.AutoGame) _game = null;
        else if (_scan == null && (now - _lastScan).TotalSeconds >= 5)
        {
            _lastScan = now;
            var exes = s.GameExes.ToList();
            var ignore = s.GameIgnore.ToList();
            bool stores = s.GameDetectStores;
            _scan = Task.Run(() => GameDetector.Find(exes, ignore, stores));
        }

        FanProfile? want = null;
        string? reason = null;
        if (_game != null)
        {
            want = s.GameProfile;
            reason = $"Spiel erkannt: {_game}";
        }
        else if (s.AutoSchedule && s.Schedule.FirstOrDefault(r => r.Active(now)) is ScheduleRule rule)
        {
            want = rule.Profile;
            reason = $"Zeitplan {rule.From}–{rule.To}";
        }

        // Selbst gewählt, während dieser Anlass lief -> bis zu seinem Ende nicht wieder eingreifen
        if (ctl.HeldReason != null)
        {
            if (reason == ctl.HeldReason) { want = null; reason = null; }
            else ctl.HeldReason = null;
        }
        // Lüftersteuerung wurde noch nie bestätigt -> nicht von selbst übernehmen
        if (want != FanProfile.Bios && !s.WarningAccepted) { want = null; reason = null; }

        if (!ctl.SetOverride(want, reason)) return null;
        return ctl.Override is FanProfile p
            ? $"Lüfterprofil „{Profiles.Get(p).Title}“ – {reason}"
            : $"Zurück zum Lüfterprofil „{Profiles.Get(ctl.ManualProfile).Title}“";
    }
}

// ===========================================================================
// Einstellungen: Abschnitt "Automatischer Profilwechsel"
// ===========================================================================
static class AutomationSettings
{
    public static void Build(SettingsView v, FanController ctl, AutoSwitcher auto)
    {
        var s = ctl.Settings;
        float k = v.DeviceDpi / 96f;
        var titles = Profiles.All.Select(p => p.Title).ToArray();
        int Index(FanProfile p) => Array.FindIndex(Profiles.All, i => i.Profile == p);

        v.AddSection("Automatischer Profilwechsel", Color.FromArgb(250, 204, 21));
        var c = v.AddCard();
        c.Note("Wechselt das Lüfterprofil von selbst – beim Spielen und/oder zu bestimmten Zeiten. Ein laufendes Spiel geht vor dem Zeitplan. " +
               "Wählst du selbst ein Profil, gilt deine Wahl, bis das Spiel beendet ist bzw. der Zeitraum endet.");
        c.Check("Beim Spielen automatisch wechseln", s.AutoGame, x => { s.AutoGame = x; ctl.Save(); });
        c.Combo("Profil beim Spielen:", titles, Index(s.GameProfile), i => { s.GameProfile = Profiles.All[i].Profile; ctl.Save(); });
        c.Check("Spiele aus Steam, Epic, GOG, Xbox, EA, Ubisoft, Riot, Battle.net automatisch erkennen",
            s.GameDetectStores, x => { s.GameDetectStores = x; ctl.Save(); });
        var listNote = c.Note(GameListText(s));
        var live = c.Note("Gerade erkannt: –");
        c.Buttons(("Spiele-Liste bearbeiten …", () =>
        {
            using var dlg = new GameListDialog(s.GameExes, s.GameIgnore);
            if (dlg.ShowDialog(v.FindForm()) != DialogResult.OK) return;
            s.GameExes = dlg.Games;
            s.GameIgnore = dlg.Ignored;
            ctl.Save();
            listNote.Text = GameListText(s);
        }));

        // Live-Anzeige, was gerade erkannt ist
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            if (!live.Visible) return;
            string t = !s.AutoGame ? "Gerade erkannt: – (ausgeschaltet)"
                     : auto.CurrentGame is string g ? $"Gerade erkannt: {g}" : "Gerade erkannt: kein Spiel";
            if (ctl.Override != null && ctl.OverrideReason != null) t += $"   ·   aktiv: {Profiles.Get(ctl.Profile).Title} ({ctl.OverrideReason})";
            else if (ctl.HeldReason != null) t += "   ·   pausiert, weil du selbst ein Profil gewählt hast";
            if (live.Text != t) live.Text = t;
        };
        timer.Start();
        live.Disposed += (_, _) => timer.Dispose();

        c.Space(8);
        c.Check("Nach Zeitplan wechseln", s.AutoSchedule, x => { s.AutoSchedule = x; ctl.Save(); });
        var sched = new ScheduleEditor(s.Schedule, () => ctl.Save()) { Margin = new Padding((int)(6 * k), 0, (int)(6 * k), (int)(4 * k)) };
        v.AddFullControl(sched);

        var n = v.AddCard();
        n.Check("Beim Wechsel einen Hinweis unten rechts zeigen", s.AutoNotify, x => { s.AutoNotify = x; ctl.Save(); });
    }

    static string GameListText(FanSettings s)
    {
        string mine = s.GameExes.Count == 0 ? "keine" : string.Join(", ", s.GameExes);
        string ign = s.GameIgnore.Count == 0 ? "" : $"   ·   Nie als Spiel: {string.Join(", ", s.GameIgnore)}";
        return $"Eigene Spiele/Programme: {mine}{ign}";
    }
}

// ---------------------------------------------------------------------------
// Zeitplan-Zeilen: von – bis, Profil, Wochentage
// ---------------------------------------------------------------------------
sealed class ScheduleEditor : Panel
{
    readonly List<ScheduleRule> _rules;
    readonly Action _save;
    static readonly string[] Times = Enumerable.Range(0, 48).Select(i => $"{i / 2:00}:{(i % 2) * 30:00}").ToArray();
    static readonly string[] DayNames = { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" };
    float K => DeviceDpi / 96f;
    int S(float px) => (int)(px * K);

    public ScheduleEditor(List<ScheduleRule> rules, Action save)
    {
        _rules = rules;
        _save = save;
        BackColor = Theme.Card;
        Rebuild();
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Rebuild();
    }

    ComboBox Combo(string[] items, int sel, int x, int y, int w)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Theme.CardHero, ForeColor = Theme.Text, Location = new Point(x, y), Width = w,
        };
        c.Items.AddRange(items);
        c.SelectedIndex = Math.Clamp(sel, 0, items.Length - 1);
        Controls.Add(c);
        return c;
    }

    Label Lbl(string t, int x, int y)
    {
        var l = new Label { Text = t, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, y + S(4)), UseMnemonic = false };
        Controls.Add(l);
        return l;
    }

    void Rebuild()
    {
        SuspendLayout();
        foreach (Control ctl in Controls.Cast<Control>().ToList()) { Controls.Remove(ctl); ctl.Dispose(); }
        int y = S(4);
        var titles = Profiles.All.Select(p => p.Title).ToArray();

        if (_rules.Count == 0)
        {
            Lbl("Noch keine Zeiträume. Beispiel: nachts 22:00–07:00 „Energiesparend“.", S(14), y);
            y += S(30);
        }

        foreach (var r in _rules.ToList())
        {
            int x = S(14);
            Lbl("von", x, y); x += S(32);
            var from = Combo(Times, Array.IndexOf(Times, r.From), x, y, S(70)); x += S(78);
            Lbl("bis", x, y); x += S(28);
            var to = Combo(Times, Array.IndexOf(Times, r.To), x, y, S(70)); x += S(84);
            var prof = Combo(titles, Array.FindIndex(Profiles.All, p => p.Profile == r.Profile), x, y, S(160)); x += S(172);
            from.SelectedIndexChanged += (_, _) => { r.From = Times[from.SelectedIndex]; _save(); };
            to.SelectedIndexChanged += (_, _) => { r.To = Times[to.SelectedIndex]; _save(); };
            prof.SelectedIndexChanged += (_, _) => { r.Profile = Profiles.All[prof.SelectedIndex].Profile; _save(); };

            for (int d = 0; d < 7; d++)
            {
                int bit = 1 << d;
                var cb = new CheckBox
                {
                    Text = DayNames[d], Checked = (r.Days & bit) != 0, ForeColor = Theme.Text, AutoSize = true,
                    Location = new Point(x, y + S(2)), UseMnemonic = false,
                };
                cb.CheckedChanged += (_, _) => { r.Days = cb.Checked ? r.Days | bit : r.Days & ~bit; _save(); };
                Controls.Add(cb);
                x += S(46);
            }
            var del = new LinkLabel
            {
                Text = "Entfernen", LinkColor = Theme.Muted, ActiveLinkColor = Theme.Text, AutoSize = true,
                LinkBehavior = LinkBehavior.HoverUnderline, Location = new Point(x + S(8), y + S(4)),
            };
            del.Click += (_, _) => { _rules.Remove(r); _save(); BeginInvoke(Rebuild); };
            Controls.Add(del);
            y += S(34);
        }

        var add = PageBar.Button("Zeitraum hinzufügen", K, 170);
        add.Location = new Point(S(14), y + S(2));
        add.Height = S(30);
        add.Click += (_, _) =>
        {
            _rules.Add(new ScheduleRule());
            _save();
            BeginInvoke(Rebuild);
        };
        Controls.Add(add);
        y += S(44);
        Height = y;
        ResumeLayout();
    }
}

// ---------------------------------------------------------------------------
// Eigene Spiele-Liste + Ausnahmen
// ---------------------------------------------------------------------------
sealed class GameListDialog : Form
{
    readonly TextBox _games, _ignore;
    public List<string> Games => Lines(_games);
    public List<string> Ignored => Lines(_ignore);

    static List<string> Lines(TextBox t) => t.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public GameListDialog(List<string> games, List<string> ignore)
    {
        Text = "Spiele-Liste";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 430);
        DarkMode.TitleBar(this);

        Label L(string t, int x, int y, int w) { var l = new Label { Text = t, ForeColor = Theme.Muted, Location = new Point(x, y), Size = new Size(w, 36), UseMnemonic = false }; Controls.Add(l); return l; }
        TextBox Box(List<string> items, int x)
        {
            var t = new TextBox
            {
                Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.Card, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, Location = new Point(x, 66), Size = new Size(285, 250),
                Text = string.Join(Environment.NewLine, items),
            };
            DarkMode.Use(t);
            Controls.Add(t);
            return t;
        }

        L("Zählen immer als Spiel (ein Programm pro Zeile, z. B. „Minecraft.exe“ oder „javaw.exe“):", 16, 14, 285);
        L("Nie als Spiel werten (z. B. ein Tool, das im Steam-Ordner liegt):", 318, 14, 285);
        _games = Box(games, 16);
        _ignore = Box(ignore, 318);

        var running = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardHero, ForeColor = Theme.Text,
            Location = new Point(16, 330), Width = 420,
        };
        var procs = GameDetector.RunningWithWindow();
        foreach (var (exe, title) in procs) running.Items.Add($"{title}  ({exe})");
        if (running.Items.Count > 0) running.SelectedIndex = 0;
        Controls.Add(running);

        Button Btn(string t, int x, int y, int w)
        {
            var b = new Button { Text = t, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardHero, ForeColor = Theme.Text, Location = new Point(x, y), Size = new Size(w, 30) };
            b.FlatAppearance.BorderColor = Theme.Border;
            Controls.Add(b);
            return b;
        }
        var add = Btn("Als Spiel hinzufügen", 444, 328, 160);
        add.Click += (_, _) =>
        {
            if (running.SelectedIndex < 0) return;
            string exe = procs[running.SelectedIndex].Exe;
            if (!Games.Contains(exe, StringComparer.OrdinalIgnoreCase))
                _games.AppendText((_games.TextLength > 0 && !_games.Text.EndsWith(Environment.NewLine) ? Environment.NewLine : "") + exe + Environment.NewLine);
        };
        L("Tipp: Spiel starten, dann hier aus den laufenden Programmen auswählen.", 16, 362, 420);

        var ok = Btn("Speichern", 384, 388, 105);
        ok.DialogResult = DialogResult.OK;
        var cancel = Btn("Abbrechen", 499, 388, 105);
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

// ---------------------------------------------------------------------------
// Kleine Text-Abfrage im dunklen Design
// ---------------------------------------------------------------------------
static class Prompt
{
    public static string? Ask(IWin32Window? owner, string title, string label, string value)
    {
        using var f = new Form
        {
            Text = title, BackColor = Theme.Background, ForeColor = Theme.Text, Font = new Font("Segoe UI", 9.5f),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(380, 132),
        };
        DarkMode.TitleBar(f);
        var l = new Label { Text = label, ForeColor = Theme.Muted, Location = new Point(16, 14), AutoSize = true, UseMnemonic = false };
        var t = new TextBox { Text = value, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Location = new Point(16, 40), Width = 348 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardHero, Location = new Point(168, 84), Size = new Size(95, 30) };
        var no = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardHero, Location = new Point(269, 84), Size = new Size(95, 30) };
        ok.FlatAppearance.BorderColor = no.FlatAppearance.BorderColor = Theme.Border;
        f.Controls.AddRange(new Control[] { l, t, ok, no });
        f.AcceptButton = ok;
        f.CancelButton = no;
        return f.ShowDialog(owner) == DialogResult.OK ? t.Text.Trim() : null;
    }
}
