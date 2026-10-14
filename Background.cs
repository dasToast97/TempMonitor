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

    // ----- Warnungen (Windows-Benachrichtigung) -----
    public bool WarnEnabled { get; set; } = true;
    public float WarnCpu { get; set; } = 90f;
    public float WarnGpu { get; set; } = 85f;
    public float WarnHotspot { get; set; } = 100f;
    public float WarnBoard { get; set; } = 70f;
    public bool WarnFanStop { get; set; } = true;
    public bool WarnSsd { get; set; } = true;
    public int WarnCooldownMinutes { get; set; } = 10;

    // ----- Infobereich & Overlay -----
    public bool TrayShowTemp { get; set; } = true;
    public string TrayTempSource { get; set; } = "CPU";   // CPU, GPU, Hotspot, Max
    public bool OverlayEnabled { get; set; }
    public int OverlayX { get; set; } = -1;
    public int OverlayY { get; set; } = -1;
    public int OverlayOpacity { get; set; } = 85;

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

// ===========================================================================
// Dunkles Windows-Design für Scrollbalken, Listen und Titelleiste
// ===========================================================================
static class DarkMode
{
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// Scrollbalken / Listen eines Steuerelements dunkel darstellen
    public static void Use(Control c)
    {
        void Apply() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
        if (c.IsHandleCreated) Apply();
        c.HandleCreated += (_, _) => Apply();
    }

    /// Dunkle Titelleiste (Windows 10 ab 1809 / Windows 11)
    public static void TitleBar(Form f)
    {
        void Apply()
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
            }
            catch { }
        }
        if (f.IsHandleCreated) Apply();
        f.HandleCreated += (_, _) => Apply();
    }

    /// Nur senkrecht scrollen – nie waagerecht
    public static void NoHorizontalScroll(ScrollableControl p)
    {
        p.AutoScroll = false;
        p.HorizontalScroll.Maximum = 0;
        p.HorizontalScroll.Enabled = false;
        p.HorizontalScroll.Visible = false;
        p.AutoScroll = true;
    }

    /// Nutzbare Breite für Elemente über die ganze Breite (Platz für den Scrollbalken freihalten)
    public static int FullWidth(ScrollableControl p, int extra) =>
        Math.Max(100, p.ClientSize.Width - p.Padding.Horizontal - extra
                      - (p.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth));
}
