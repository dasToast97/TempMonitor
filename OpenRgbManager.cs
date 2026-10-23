using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TempMonitor;

/// <summary>
/// OpenRGB (Open Source, GPL-2.0, https://openrgb.org) als Helfer: einmal mit Zustimmung von der
/// offiziellen Projektseite (codeberg.org/OpenRGB) laden, danach automatisch unsichtbar mit
/// SDK-Server starten und beim Beenden wieder schließen. Toast Temperatur spricht OpenRGB über
/// dessen SDK an (Port 6742) – so laufen die Effekte auch auf Geräten, die nur OpenRGB sauber steuert.
/// </summary>
static class OpenRgbManager
{
    const string Api = "https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases?limit=10";
    public const string ProjectUrl = "https://openrgb.org";

    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TempMonitor", "OpenRGB");

    static Process? _proc;
    public static string Status { get; private set; } = "";

    public static string? ExePath
    {
        get
        {
            try
            {
                return Directory.Exists(Dir)
                    ? Directory.EnumerateFiles(Dir, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault()
                    : null;
            }
            catch { return null; }
        }
    }

    public static bool Installed => ExePath != null;

    static bool RunningElsewhere()
    {
        try
        {
            return Process.GetProcessesByName("OpenRGB").Any(p =>
            {
                using (p) return _proc == null || p.Id != _proc.Id;
            });
        }
        catch { return false; }
    }

    /// Neueste Windows-64-Bit-Version laden und entpacken
    public static async Task<bool> InstallAsync(IProgress<string>? progress = null)
    {
        try
        {
            progress?.Report("Suche die neueste OpenRGB-Version …");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TempMonitor", Updater.CurrentVersion.ToString(3)));
            var json = await http.GetStringAsync(Api);
            using var doc = JsonDocument.Parse(json);
            string? url = null, name = null, tag = null;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                if (rel.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) continue;
                foreach (var a in rel.GetProperty("assets").EnumerateArray())
                {
                    string n = a.GetProperty("name").GetString() ?? "";
                    bool win64 = n.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                                 && n.Contains("64", StringComparison.Ordinal)
                                 && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                 && !n.Contains("32", StringComparison.Ordinal)
                                 && !n.Contains("arm", StringComparison.OrdinalIgnoreCase);
                    if (!win64) continue;
                    url = a.GetProperty("browser_download_url").GetString();
                    name = n;
                    tag = rel.GetProperty("tag_name").GetString();
                    break;
                }
                if (url != null) break;
            }
            if (url == null || !url.StartsWith("https://codeberg.org/", StringComparison.OrdinalIgnoreCase))
            {
                Status = "Keine passende OpenRGB-Version gefunden";
                return false;
            }

            progress?.Report($"Lade {name} …");
            string tmp = Path.Combine(Path.GetTempPath(), "TempMonitor-OpenRGB.zip");
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst);
            }

            progress?.Report("Entpacke …");
            Stop();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            ZipFile.ExtractToDirectory(tmp, Dir, overwriteFiles: true);
            try { File.Delete(tmp); } catch { }
            File.WriteAllText(Path.Combine(Dir, "QUELLE.txt"),
                $"OpenRGB {tag} – Open Source (GPL-2.0), unverändert von {url}{Environment.NewLine}" +
                $"Quelltext: https://codeberg.org/OpenRGB/OpenRGB (Tag {tag}) · {ProjectUrl}{Environment.NewLine}");

            if (!Installed) { Status = "OpenRGB.exe nicht im Paket gefunden"; return false; }
            Status = $"OpenRGB {tag} eingerichtet";
            RgbSettings.Log(Status);
            return true;
        }
        catch (Exception ex)
        {
            Status = "Einrichten fehlgeschlagen: " + ex.Message;
            RgbSettings.Log("OpenRGB: " + Status);
            return false;
        }
    }

    /// Unsichtbar mit SDK-Server starten (nicht, wenn OpenRGB schon läuft)
    public static void Start()
    {
        try
        {
            if (_proc is { HasExited: false }) return;
            var exe = ExePath;
            if (exe == null) return;
            // Von einem früheren Toast Temperatur übrig gebliebenes OpenRGB (z. B. nach Absturz oder Energiesparmodus)
            // kennt oft die USB-Geräte nicht mehr -> beenden und frisch starten. Fremd gestartetes OpenRGB bleibt unangetastet.
            foreach (var p in Process.GetProcessesByName("OpenRGB"))
            {
                using (p)
                {
                    try
                    {
                        if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(3000);
                            RgbSettings.Log("Altes OpenRGB beendet (neu starten)");
                        }
                    }
                    catch { }
                }
            }
            if (RunningElsewhere()) return;
            _proc = Process.Start(new ProcessStartInfo(exe, "--server --startminimized")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            Status = "OpenRGB läuft (von Toast Temperatur gestartet)";
            RgbSettings.Log("OpenRGB gestartet");
        }
        catch (Exception ex)
        {
            Status = "OpenRGB startet nicht: " + ex.Message;
            RgbSettings.Log(Status);
        }
    }

    /// Nur das selbst gestartete OpenRGB beenden
    public static void Stop()
    {
        try
        {
            if (_proc is { HasExited: false } p)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch { }
        _proc = null;
    }
}
