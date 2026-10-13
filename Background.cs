using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TempMonitor;

// ===========================================================================
// Allgemeine Einstellungen (%AppData%\TempMonitor\app.json)
// ===========================================================================
sealed class AppSettings
{
    public bool LogEnabled { get; set; } = true;
    public int LogIntervalSeconds { get; set; } = 5;
    public bool CloseHintShown { get; set; }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "app.json");

    public static AppSettings Instance { get; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
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
}

// ===========================================================================
// Aufzeichnung: eine CSV-Datei pro Tag in "Dokumente\Toast Temperatur"
// (Semikolon + deutsches Komma -> öffnet sich direkt sauber in Excel)
// ===========================================================================
sealed class DataLogger
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Toast Temperatur");

    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    readonly List<(string Header, Func<float?> Value)> _columns = new();
    DateTime _last = DateTime.MinValue;
    string? _currentFile;
    string _headerLine = "";

    public string? LastError { get; private set; }
    public bool Enabled => AppSettings.Instance.LogEnabled;

    public void Clear() => _columns.Clear();

    public void Add(string header, Func<float?> value)
    {
        // Doppelte Spaltennamen eindeutig machen
        string h = header.Replace(";", ",");
        int n = 2;
        while (_columns.Any(c => c.Header == h)) h = $"{header} ({n++})";
        _columns.Add((h, value));
        _headerLine = "Zeit;" + string.Join(";", _columns.Select(c => c.Header));
    }

    /// Wird jede Sekunde aufgerufen; schreibt im eingestellten Abstand
    public void Tick()
    {
        if (!Enabled || _columns.Count == 0) return;
        var now = DateTime.Now;
        if ((now - _last).TotalSeconds < Math.Max(1, AppSettings.Instance.LogIntervalSeconds)) return;
        _last = now;

        try
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, $"Toast-Temperatur_{now:yyyy-MM-dd}.csv");

            if (file != _currentFile)
            {
                _currentFile = file;
                // Neue Datei oder geänderte Spalten (andere Hardware): Kopfzeile schreiben
                bool needHeader = !File.Exists(file) || File.ReadLines(file).FirstOrDefault() != _headerLine;
                if (needHeader)
                {
                    if (File.Exists(file)) File.AppendAllText(file, Environment.NewLine, Encoding.UTF8);
                    File.AppendAllText(file, _headerLine + Environment.NewLine, new UTF8Encoding(true));
                }
            }

            var sb = new StringBuilder(now.ToString("dd.MM.yyyy HH:mm:ss", De));
            foreach (var (_, value) in _columns)
            {
                sb.Append(';');
                if (value() is float v && !float.IsNaN(v)) sb.Append(v.ToString("0.0", De));
            }
            sb.AppendLine();
            File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
        }
        catch { }
    }
}

// ===========================================================================
// Autostart über die Aufgabenplanung (startet mit Adminrechten, ohne UAC-Abfrage,
// unsichtbar im Infobereich)
// ===========================================================================
static class Autostart
{
    const string TaskName = "TempMonitor";

    static int Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return -1;
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    public static bool IsEnabled => Run("schtasks.exe", $"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enable()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        int r = Run("schtasks.exe",
            $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" /tray\" /SC ONLOGON /RL HIGHEST");
        if (r != 0) return false;
        // Kein 72-Stunden-Zeitlimit, auch im Akkubetrieb laufen lassen
        Run("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 " +
            $"-AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; Set-ScheduledTask -TaskName '{TaskName}' -Settings $s\"");
        return true;
    }

    public static bool Disable() => Run("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"") == 0;
}

// ===========================================================================
// Zweiter Start -> vorhandenes Fenster nach vorne holen
// ===========================================================================
static class SingleInstance
{
    const string EventName = "TempMonitor_ShowWindow";

    /// Vom zweiten Start aufgerufen: dem laufenden Programm Bescheid geben
    public static void SignalFirst()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(EventName);
            ev.Set();
        }
        catch { }
    }

    /// Im laufenden Programm: auf Signal warten und dann das Fenster zeigen
    public static void Listen(Action onShow)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var t = new Thread(() =>
        {
            while (true)
            {
                ev.WaitOne();
                onShow();
            }
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        t.Start();
    }
}
