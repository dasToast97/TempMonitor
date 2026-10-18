using System.Diagnostics;
using System.Globalization;

namespace TempMonitor;

/// Lüfter/Pumpe, die nicht über LibreHardwareMonitor laufen (z. B. XMG Oasis, Laptop-Lüfter)
interface IExternalFan
{
    void SetPercent(float pct);
    /// Steuerung abgeben (Standardwerte setzen)
    void Release();
    bool Ready { get; }
    float? LastPercent { get; }
    /// Warum nicht bereit (Suche, Fehler …)
    string? Problem { get; }
    /// Ernstes Problem (z. B. Durchfluss gestört) -> Warnung
    string? Alarm { get; }
    /// Hinweis, der an den Status angehängt wird
    string? Note { get; }
}

// ===========================================================================
// XMG OASIS: Die Bluetooth-Verbindung läuft im eigenen Hilfsprogramm OasisBridge.exe
// (liegt neben TempMonitor.exe). Toast Temperatur schickt ihm Befehle und liest den Zustand.
// So kann ein Bluetooth-Problem Toast Temperatur nie zum Absturz bringen.
// ===========================================================================
sealed class OasisLink
{
    public static OasisLink? Instance { get; private set; }

    readonly FanSettings _s;
    readonly object _gate = new();
    Process? _proc;
    DateTime _lastStart = DateTime.MinValue;
    int _restarts;
    float _fan = -1, _pump = -1;
    float _lastFanSent = -2, _lastPumpSent = -2;
    int _lastVolt = -1;
    float _lastRelFan = -1, _lastRelPump = -1;
    System.Threading.Timer? _timer;
    DateTime _lastCcCheck = DateTime.MinValue;

    public volatile bool Connected;
    public string State { get; private set; } = "Starte Bluetooth …";
    public string? Model { get; private set; }
    public string? Firmware { get; private set; }
    public int Flow { get; private set; } = -1;
    public bool ControlCenterRunning { get; private set; }
    public float? FanPercent { get; private set; }
    public float? PumpPercent { get; private set; }

    /// Gerät zum ersten Mal gefunden (Adresse)
    public event Action<ulong>? Found;

    static string BridgePath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath) ?? ".", "OasisBridge.exe");
    public static bool Available => File.Exists(BridgePath);

    public OasisLink(FanSettings s)
    {
        _s = s;
        Instance = this;
    }

    public void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Pump(), null, 0, 1000);
    }

    public void SetFan(float pct) { lock (_gate) _fan = Math.Clamp(pct, 0, 100); }
    public void SetPump(float pct) { lock (_gate) _pump = Math.Clamp(pct, 0, 100); }

    /// LED der Oasis setzen: 0x1E = Pumpenkopf, 0x33 = Lüfter
    public void SetLed(int zone, Color c) => Send($"led {zone} {c.R} {c.G} {c.B}");

    public void Forget()
    {
        _s.OasisAddress = null;
        Send("forget");
        Connected = false;
    }

    void Send(string line)
    {
        try
        {
            var p = _proc;
            if (p == null || p.HasExited) return;
            p.StandardInput.WriteLine(line);
            p.StandardInput.Flush();
        }
        catch { }
    }

    /// Jede Sekunde: Hilfsprogramm am Leben halten, geänderte Werte schicken
    void Pump()
    {
        try
        {
            if ((DateTime.Now - _lastCcCheck).TotalSeconds >= 10)
            {
                _lastCcCheck = DateTime.Now;
                ControlCenterRunning = Process.GetProcesses().Any(p =>
                {
                    using (p)
                    {
                        string n = p.ProcessName;
                        return n.Equals("GCUService", StringComparison.OrdinalIgnoreCase)
                               || n.Contains("ControlCenter", StringComparison.OrdinalIgnoreCase);
                    }
                });
            }

            if (_proc == null || _proc.HasExited)
            {
                Connected = false;
                if (!Available) { State = "OasisBridge.exe fehlt – bitte Toast Temperatur neu installieren"; return; }
                // Abgestürzt? Nicht endlos neu starten
                double wait = _restarts < 3 ? 5 : 120;
                if ((DateTime.Now - _lastStart).TotalSeconds < wait) return;
                StartBridge();
                return;
            }

            float fan, pump;
            lock (_gate) { fan = _fan; pump = _pump; }
            int volt = Math.Clamp(_s.OasisPumpVoltage, 0, 3);
            if (volt != _lastVolt) { Send($"volt {volt}"); _lastVolt = volt; }
            if (_s.OasisReleaseFan != _lastRelFan || _s.OasisReleasePump != _lastRelPump)
            {
                Send(FormattableString.Invariant($"release {_s.OasisReleaseFan:0} {_s.OasisReleasePump:0}"));
                _lastRelFan = _s.OasisReleaseFan;
                _lastRelPump = _s.OasisReleasePump;
            }
            if (fan >= 0 && Math.Abs(fan - _lastFanSent) >= 0.5f) { Send(FormattableString.Invariant($"fan {fan:0.#}")); _lastFanSent = fan; }
            if (pump >= 0 && Math.Abs(pump - _lastPumpSent) >= 0.5f) { Send(FormattableString.Invariant($"pump {pump:0.#}")); _lastPumpSent = pump; }
        }
        catch (Exception ex) { ErrorLog.Write("XMG Oasis", ex); }
    }

    void StartBridge()
    {
        _lastStart = DateTime.Now;
        _restarts++;
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(BridgePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
            p.Exited += (_, _) => { Connected = false; State = "Bluetooth-Hilfsprogramm beendet – starte neu …"; };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
            _lastVolt = -1;
            _lastRelFan = _lastRelPump = -1;
            _lastFanSent = _lastPumpSent = -2;
            Send(_s.OasisAddress is ulong a ? $"addr {a:X12}" : "addr -");
            State = "Suche XMG Oasis …";
        }
        catch (Exception ex)
        {
            State = "Bluetooth-Hilfsprogramm startet nicht: " + ex.Message;
            ErrorLog.Write("OasisBridge starten", ex);
        }
    }

    void OnLine(string line)
    {
        try
        {
            int sp = line.IndexOf(' ');
            string cmd = sp < 0 ? line : line[..sp];
            string arg = sp < 0 ? "" : line[(sp + 1)..];
            switch (cmd)
            {
                case "state": State = arg; break;
                case "connected":
                    var parts = arg.Split('\t');
                    Model = parts[0];
                    Firmware = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                    Connected = true;
                    _restarts = 0;
                    _lastFanSent = _lastPumpSent = -2;   // nach (Neu-)Verbindung alles erneut schicken
                    break;
                case "disconnected": Connected = false; break;
                case "flow": if (int.TryParse(arg, out var f)) Flow = f; break;
                case "found":
                    if (ulong.TryParse(arg, NumberStyles.HexNumber, null, out var addr))
                    {
                        _s.OasisAddress = addr;
                        Found?.Invoke(addr);
                    }
                    break;
                case "sent":
                    var v = arg.Split(' ');
                    if (v.Length >= 2)
                    {
                        if (float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sf) && sf >= 0) FanPercent = sf;
                        if (float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var spp) && spp >= 0) PumpPercent = spp;
                    }
                    break;
            }
        }
        catch { }
    }

    /// Beim Beenden: Standardwerte senden lassen, Verbindung sauber abmelden
    public void Shutdown()
    {
        try
        {
            _timer?.Dispose();
            var p = _proc;
            if (p == null || p.HasExited) return;
            Send(FormattableString.Invariant($"release {_s.OasisReleaseFan:0} {_s.OasisReleasePump:0}"));
            Send("exit");
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
        }
        catch { }
    }
}

/// Lüfter oder Pumpe der Oasis als Kanal für die Lüftersteuerung
sealed class OasisFan : IExternalFan
{
    readonly OasisLink _link;
    readonly FanSettings _s;
    readonly bool _pump;

    public OasisFan(OasisLink link, FanSettings s, bool pump)
    {
        _link = link;
        _s = s;
        _pump = pump;
    }

    public void SetPercent(float pct)
    {
        if (_pump) _link.SetPump(pct);
        else _link.SetFan(pct);
    }

    public void Release()
    {
        if (_pump) _link.SetPump(_s.OasisReleasePump);
        else _link.SetFan(_s.OasisReleaseFan);
    }

    public bool Ready => _link.Connected;
    public float? LastPercent => _pump ? _link.PumpPercent : _link.FanPercent;
    public string? Problem => _link.Connected ? null : _link.State;

    public string? Alarm => _pump && _link.Connected && _link.Flow >= 0 && _link.Flow != 2
        ? "Oasis meldet: Durchfluss gestört – Schläuche, Schnellkupplungen und Wasserstand prüfen"
        : null;

    public string? Note => _link.ControlCenterRunning
        ? "Control Center läuft auch – Toast Temperatur setzt seine Werte alle 5 s durch"
        : null;
}
