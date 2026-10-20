using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace TempMonitor;

/// <summary>
/// Prüft GitHub Releases auf eine neue Version, fragt den Nutzer und
/// ersetzt die laufende .exe durch die neue (danach Neustart).
/// </summary>
static class Updater
{
    const string AssetName = "TempMonitor-Setup.exe";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string? Repo =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepo")?.Value is { Length: > 0 } r ? r : null;

    static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor");
    static readonly string SkipFile = Path.Combine(SettingsDir, "skip-version.txt");

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TempMonitor", CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public record ReleaseInfo(Version Version, string Tag, string Notes, string DownloadUrl, string PageUrl);

    static string? _etag, _lastJson;

    static async Task<ReleaseInfo?> GetLatestAsync()
    {
        if (Repo is null) return null;
        using var http = CreateClient();
        // Bedingte Abfrage: „nicht geändert“ (304) zählt nicht gegen GitHubs Grenze von 60 Abfragen pro Stunde
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        if (_etag != null && _lastJson != null) req.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        using var resp = await http.SendAsync(req);
        string json;
        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && _lastJson != null) json = _lastJson;
        else
        {
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync();
            _lastJson = json;
            _etag = resp.Headers.ETag?.ToString();
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;

        string? url = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
            if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                url = a.GetProperty("browser_download_url").GetString();
        if (url is null) return null;

        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new ReleaseInfo(ver, tag, notes, url, page);
    }

    static bool IsNewer(Version remote) =>
        new Version(remote.Major, remote.Minor, Math.Max(remote.Build, 0)) >
        new Version(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(CurrentVersion.Build, 0));

    static bool _dialogOpen;
    static string? _laterTag;
    static DateTime _laterUntil;

    /// Regelmäßige Prüfung im Hintergrund: Gibt es eine Version, die jetzt angeboten werden soll?
    /// (nicht übersprungen, nicht gerade mit „Später“ zurückgestellt, kein Dialog offen)
    public static async Task<ReleaseInfo?> PeekAsync()
    {
        if (Repo is null || _dialogOpen) return null;
        ReleaseInfo? rel;
        try { rel = await GetLatestAsync(); } catch { return null; }
        if (rel is null || !IsNewer(rel.Version) || ReadSkipped() == rel.Tag) return null;
        if (_laterTag == rel.Tag && DateTime.Now < _laterUntil) return null;
        return rel;
    }

    /// Bekannte neue Version anbieten (z. B. nach Klick auf die Benachrichtigung)
    public static Task OfferAsync(IWin32Window owner, ReleaseInfo rel) => AskAsync(owner, rel, manual: false);

    /// <param name="manual">true = vom Nutzer per Knopf ausgelöst (zeigt auch "kein Update" / Fehler an)</param>
    public static async Task CheckAsync(IWin32Window owner, bool manual)
    {
        if (_dialogOpen) return;
        if (Repo is null)
        {
            if (manual)
                MessageBox.Show(owner, "Diese Version wurde lokal gebaut und hat keine Update-Quelle.\n" +
                    "Updates gibt es nur für Versionen aus den GitHub Releases.", "Updates",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ReleaseInfo? rel;
        try { rel = await GetLatestAsync(); }
        catch (Exception ex)
        {
            if (manual)
                MessageBox.Show(owner, "Update-Prüfung fehlgeschlagen:\n" + ex.Message, "Updates",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (rel is null || !IsNewer(rel.Version))
        {
            if (manual)
                MessageBox.Show(owner, $"Du hast bereits die neueste Version ({CurrentVersion.ToString(3)}).",
                    "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Automatische Prüfung: übersprungene Version nicht erneut anbieten
        if (!manual && ReadSkipped() == rel.Tag) return;
        await AskAsync(owner, rel, manual);
    }

    static async Task AskAsync(IWin32Window owner, ReleaseInfo rel, bool manual)
    {
        if (_dialogOpen) return;

        var plain = string.IsNullOrWhiteSpace(rel.Notes) ? "" : Plain(rel.Notes);
        var notes = plain.Length > 1500 ? plain[..1500] + " …" : plain;

        var btnInstall = new TaskDialogButton("Jetzt aktualisieren");
        var btnLater = new TaskDialogButton("Später");
        var btnSkip = new TaskDialogButton("Diese Version überspringen");

        var page = new TaskDialogPage
        {
            Caption = "Update verfügbar",
            Heading = $"Version {rel.Version.ToString(3)} ist verfügbar",
            Text = $"Installiert: {CurrentVersion.ToString(3)}\n\n{notes}",
            Icon = TaskDialogIcon.Information,
            Buttons = { btnInstall, btnLater, btnSkip },
            DefaultButton = btnInstall,
            AllowCancel = true,
        };

        TaskDialogButton result;
        _dialogOpen = true;
        try { result = TaskDialog.ShowDialog(owner, page); }
        finally { _dialogOpen = false; }
        if (result == btnSkip) { WriteSkipped(rel.Tag); return; }
        if (result != btnInstall)
        {
            // „Später“: dieselbe Version erst in 2 Stunden wieder anbieten
            _laterTag = rel.Tag;
            _laterUntil = DateTime.Now.AddHours(2);
            return;
        }

        _dialogOpen = true;   // während Download/Installation keine weitere Update-Abfrage
        try { await InstallAsync(owner, rel); }
        finally { _dialogOpen = false; }
    }

    /// Release-Text von GitHub (Markdown) lesbar machen: **fett**, ## Überschriften, Links
    static string Plain(string md)
    {
        var s = md.Replace("\r", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^#+\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^\s*[-*]\s+", "• ", System.Text.RegularExpressions.RegexOptions.Multiline);
        return s.Trim();
    }

    static async Task InstallAsync(IWin32Window owner, ReleaseInfo rel)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "TempMonitorUpdate");
        Directory.CreateDirectory(tempDir);
        string newExe = Path.Combine(tempDir, AssetName);

        using var progress = new ProgressForm($"Lade Version {rel.Version.ToString(3)} herunter …");
        progress.Show(owner);
        try
        {
            using var http = CreateClient();
            using var resp = await http.GetAsync(rel.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(newExe))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress.SetPercent((int)(done * 100 / total.Value));
                }
            }

            if (new FileInfo(newExe).Length < 100_000)
                throw new InvalidDataException("Die heruntergeladene Datei ist unvollständig.");
        }
        catch (Exception ex)
        {
            progress.Close();
            MessageBox.Show(owner, "Download fehlgeschlagen:\n" + ex.Message, "Update",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        progress.Close();

        // Kleines Skript: wartet bis dieses Programm beendet ist, führt dann den Installer
        // still aus. Der Installer startet das Programm danach selbst wieder.
        string script = Path.Combine(tempDir, "update.cmd");
        int pid = Environment.ProcessId;
        File.WriteAllText(script, $"""
            @echo off
            :wait
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
            "{newExe}" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS
            del "{newExe}" >nul 2>&1
            (goto) 2>nul & del "%~f0"
            """);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        Application.Exit();
    }

    static string? ReadSkipped()
    {
        try { return File.Exists(SkipFile) ? File.ReadAllText(SkipFile).Trim() : null; }
        catch { return null; }
    }

    static void WriteSkipped(string tag)
    {
        try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(SkipFile, tag); }
        catch { /* nicht wichtig */ }
    }
}

/// <summary>Kleines Fenster mit Fortschrittsbalken während des Downloads.</summary>
sealed class ProgressForm : Form
{
    readonly ProgressBar _bar;

    public ProgressForm(string text)
    {
        float k = Dpi.K(this);
        Text = "Update";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ControlBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Header;
        ForeColor = Theme.Text;
        ClientSize = new Size((int)(380 * k), (int)(90 * k));

        var label = new Label
        {
            Text = text,
            Location = new Point((int)(16 * k), (int)(14 * k)),
            AutoSize = true,
        };
        _bar = new ProgressBar
        {
            Location = new Point((int)(16 * k), (int)(44 * k)),
            Size = new Size((int)(348 * k), (int)(22 * k)),
            Style = ProgressBarStyle.Marquee,
        };
        Controls.Add(label);
        Controls.Add(_bar);
    }

    public void SetPercent(int p)
    {
        if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = Math.Clamp(p, 0, 100);
    }
}
