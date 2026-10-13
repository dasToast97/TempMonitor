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

    record ReleaseInfo(Version Version, string Tag, string Notes, string DownloadUrl, string PageUrl);

    static async Task<ReleaseInfo?> GetLatestAsync()
    {
        if (Repo is null) return null;
        using var http = CreateClient();
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
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

    /// <param name="manual">true = vom Nutzer per Knopf ausgelöst (zeigt auch "kein Update" / Fehler an)</param>
    public static async Task CheckAsync(IWin32Window owner, bool manual)
    {
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

        var notes = string.IsNullOrWhiteSpace(rel.Notes) ? "" :
            (rel.Notes.Length > 800 ? rel.Notes[..800] + " …" : rel.Notes);

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

        var result = TaskDialog.ShowDialog(owner, page);
        if (result == btnSkip) { WriteSkipped(rel.Tag); return; }
        if (result != btnInstall) return;

        await InstallAsync(owner, rel);
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
        float k = DeviceDpi / 96f;
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
