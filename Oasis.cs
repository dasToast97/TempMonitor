using System.Diagnostics;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace TempMonitor;

/// Lüfter/Pumpe, die nicht über LibreHardwareMonitor laufen (z. B. XMG Oasis über Bluetooth)
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
// XMG OASIS (Uniwill LCT21001 / LCT22002) über Bluetooth LE (Nordic UART)
// Rahmen: FE <cmd> <an/aus> <p1> <p2> <p3> <p4> EF
//   1B = Lüfter (p1 = 0–100 %), 1C = Pumpe (p1 = 0–100 %, p2 = Spannung), 32 = Durchfluss abfragen
// ===========================================================================
sealed class OasisLink
{
    static readonly Guid Nus = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    static readonly Guid TxId = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    static readonly Guid RxId = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");

    public static OasisLink? Instance { get; private set; }

    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "oasis.log");

    readonly FanSettings _s;
    readonly object _gate = new();
    BluetoothLEDevice? _dev;
    GattCharacteristic? _tx, _rx;
    TaskCompletionSource<string>? _version;
    CancellationTokenSource? _cts;

    float _fan = -1, _pump = -1;          // gewünscht
    float _sentFan = -1, _sentPump = -1;  // zuletzt gesendet
    int _sentVolt = -1;
    DateTime _lastSend = DateTime.MinValue, _lastQuery = DateTime.MinValue;

    public volatile bool Connected;
    public string State { get; private set; } = "Suche XMG Oasis …";
    public string? Model { get; private set; }
    public string? Firmware { get; private set; }
    /// Durchfluss-Status vom Gerät: 2 = in Ordnung, -1 = unbekannt
    public int Flow { get; private set; } = -1;
    public bool ControlCenterRunning { get; private set; }
    public float? FanPercent => _sentFan >= 0 ? _sentFan : null;
    public float? PumpPercent => _sentPump >= 0 ? _sentPump : null;

    /// Gerät zum ersten Mal gefunden (Adresse, Modell)
    public event Action<ulong>? Found;

    public static string VoltageText(int code) => code switch { 1 => "12 V", 2 => "7 V", 3 => "8 V", _ => "11 V" };

    public OasisLink(FanSettings s)
    {
        _s = s;
        Instance = this;
    }

    static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(() => Loop(ct));
    }

    public void SetFan(float pct) { lock (_gate) _fan = Math.Clamp(pct, 0, 100); }
    public void SetPump(float pct) { lock (_gate) _pump = Math.Clamp(pct, 0, 100); }

    /// Neu suchen (z. B. anderes Gerät)
    public void Forget()
    {
        _s.OasisAddress = null;
        Disconnect();
    }

    async Task Loop(CancellationToken ct)
    {
        int misses = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ControlCenterRunning = Process.GetProcesses().Any(p =>
                {
                    using (p)
                    {
                        string n = p.ProcessName;
                        return n.Equals("GCUService", StringComparison.OrdinalIgnoreCase)
                               || n.Contains("ControlCenter", StringComparison.OrdinalIgnoreCase)
                               || n.Contains("Control Center", StringComparison.OrdinalIgnoreCase);
                    }
                });
                if (!Connected)
                {
                    ulong? addr = _s.OasisAddress;
                    if (addr == null)
                    {
                        State = "Suche XMG Oasis …";
                        addr = await Find(ct);
                        if (addr == null)
                        {
                            misses++;
                            State = "Nicht gefunden – Oasis eingeschaltet, Bluetooth am Laptop an?";
                            await Task.Delay(misses < 3 ? 20_000 : 120_000, ct);
                            continue;
                        }
                        _s.OasisAddress = addr;
                        Found?.Invoke(addr.Value);
                    }
                    State = "Verbinde …";
                    await Connect(addr.Value);
                    misses = 0;
                }

                await SendPending();
                if ((DateTime.Now - _lastQuery).TotalSeconds >= 10)
                {
                    _lastQuery = DateTime.Now;
                    await Write(Frame(0x32, 0, 0, 0, 0, 0));
                }
                await Task.Delay(300, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("Fehler: " + ex.Message);
                State = "Verbindung verloren – verbinde neu … (" + ex.Message + ")";
                Disconnect();
                try { await Task.Delay(8000, ct); } catch { break; }
            }
        }
    }

    /// Erst bei Windows bekannte Geräte, dann 15 s nach Werbe-Paketen suchen
    async Task<ulong?> Find(CancellationToken ct)
    {
        try
        {
            foreach (bool paired in new[] { true, false })
            {
                var list = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(paired));
                foreach (var d in list)
                {
                    if (d.Name?.StartsWith("CoolingSystem", StringComparison.OrdinalIgnoreCase) != true) continue;
                    using var dev = await BluetoothLEDevice.FromIdAsync(d.Id);
                    if (dev == null) continue;
                    Model = d.Name;
                    Log($"Bekanntes Gerät: {d.Name} {dev.BluetoothAddress:X12}");
                    return dev.BluetoothAddress;
                }
            }
        }
        catch (Exception ex) { Log("Geräteliste: " + ex.Message); }

        var tcs = new TaskCompletionSource<ulong?>();
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        w.Received += (_, a) =>
        {
          try
          {
            string name = a.Advertisement.LocalName ?? "";
            bool nus = a.Advertisement.ServiceUuids.Contains(Nus);
            if (name.StartsWith("CoolingSystem", StringComparison.OrdinalIgnoreCase) || (nus && name.Contains("LCT", StringComparison.OrdinalIgnoreCase)))
            {
                Model = name;
                Log($"Gefunden: {name} {a.BluetoothAddress:X12} ({a.RawSignalStrengthInDBm} dBm)");
                tcs.TrySetResult(a.BluetoothAddress);
            }
          }
          catch { }
        };
        w.Stopped += (_, a) => { if (a.Error != BluetoothError.Success) { Log("Suche gestoppt: " + a.Error); tcs.TrySetResult(null); } };
        w.Start();
        var done = await Task.WhenAny(tcs.Task, Task.Delay(15_000, ct));
        try { w.Stop(); } catch { }
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    async Task Connect(ulong addr)
    {
        Disconnect();
        var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr)
                  ?? throw new IOException("Gerät nicht erreichbar");
        _dev = dev;
        Model ??= dev.Name;
        var sr = await dev.GetGattServicesForUuidAsync(Nus, BluetoothCacheMode.Uncached);
        if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0)
            throw new IOException("Steuerdienst nicht gefunden (" + sr.Status + ")");
        var svc = sr.Services[0];
        _tx = (await svc.GetCharacteristicsForUuidAsync(TxId)).Characteristics.FirstOrDefault()
              ?? throw new IOException("Sende-Kanal fehlt");
        _rx = (await svc.GetCharacteristicsForUuidAsync(RxId)).Characteristics.FirstOrDefault()
              ?? throw new IOException("Empfangs-Kanal fehlt");
        _rx.ValueChanged += OnNotify;
        var st = await _rx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (st != GattCommunicationStatus.Success) throw new IOException("Benachrichtigungen nicht möglich (" + st + ")");

        // Handschlag: "sw" -> Gerät antwortet mit Firmware-Text
        _version = new TaskCompletionSource<string>();
        await Write(new byte[] { 0x73, 0x77 });
        var done = await Task.WhenAny(_version.Task, Task.Delay(4000));
        if (done != _version.Task) Log("Keine Antwort auf den Handschlag – versuche trotzdem zu steuern");

        dev.ConnectionStatusChanged += (d, _) =>
        {
            try { _ = d.ConnectionStatus; } catch { return; }
            if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected && Connected)
            {
                Log("Verbindung getrennt");
                Connected = false;
                State = "Getrennt – verbinde neu …";
            }
        };
        _sentFan = _sentPump = -1;
        _sentVolt = -1;
        Connected = true;
        State = "Verbunden";
        Log($"Verbunden mit {Model} ({addr:X12}), Firmware {Firmware ?? "?"}");
    }

    void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var r = DataReader.FromBuffer(args.CharacteristicValue);
            var b = new byte[r.UnconsumedBufferLength];
            r.ReadBytes(b);
            if (b.Length >= 8 && b[0] == 0xFE && (b[1] == 0x31 || b[1] == 0x32))
            {
                if (Flow != b[3]) Log($"Durchfluss-Status {b[3]}");
                Flow = b[3];
            }
            else if (b.Length > 0 && b[0] != 0xFE)
            {
                string s = Encoding.UTF8.GetString(b).Trim('\0', ' ', '\r', '\n');
                Firmware = s.Length > 17 ? s[17..].Trim() : s;
                _version?.TrySetResult(s);
            }
        }
        catch { }
    }

    static byte[] Frame(byte cmd, byte on, byte p1, byte p2, byte p3, byte p4) =>
        new byte[] { 0xFE, cmd, on, p1, p2, p3, p4, 0xEF };

    async Task Write(byte[] data)
    {
        var tx = _tx ?? throw new IOException("Nicht verbunden");
        var w = new DataWriter();
        w.WriteBytes(data);
        var opt = tx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        var res = await tx.WriteValueWithResultAsync(w.DetachBuffer(), opt);
        if (res.Status != GattCommunicationStatus.Success)
            throw new IOException("Senden fehlgeschlagen (" + res.Status + ")");
    }

    async Task SendPending()
    {
        float fan, pump;
        lock (_gate) { fan = _fan; pump = _pump; }
        int volt = Math.Clamp(_s.OasisPumpVoltage, 0, 3);
        // Alle 30 s erneut senden, falls das Control Center dazwischengefunkt hat
        // Läuft das Control Center, alle 5 s – so setzt sich Toast Temperatur durch
        bool keepAlive = (DateTime.Now - _lastSend).TotalSeconds >= (ControlCenterRunning ? 5 : 30);

        if (fan >= 0 && (Math.Abs(fan - _sentFan) >= 1 || keepAlive))
        {
            byte d = (byte)Math.Round(fan);
            await Write(Frame(0x1B, (byte)(d > 0 ? 1 : 0), d, 0, 0, 0));
            _sentFan = fan;
            _lastSend = DateTime.Now;
        }
        if (pump >= 0 && (Math.Abs(pump - _sentPump) >= 1 || volt != _sentVolt || keepAlive))
        {
            byte d = (byte)Math.Round(Math.Max(pump, 1));   // Pumpe nie ausschalten
            await Write(Frame(0x1C, 1, d, (byte)volt, 0, 0));
            _sentPump = pump;
            _sentVolt = volt;
            _lastSend = DateTime.Now;
        }
    }

    void Disconnect()
    {
        Connected = false;
        try { if (_rx != null) _rx.ValueChanged -= OnNotify; } catch { }
        _tx = _rx = null;
        try { _dev?.Dispose(); } catch { }
        _dev = null;
    }

    /// Beim Beenden: Standardwerte senden, Verbindung sauber abmelden
    public void Shutdown()
    {
        try
        {
            _cts?.Cancel();
            if (!Connected) return;
            Task.Run(async () =>
            {
                lock (_gate) { _fan = _s.OasisReleaseFan; _pump = _s.OasisReleasePump; }
                _lastSend = DateTime.MinValue;
                await SendPending();
                await Write(Frame(0x38, 0, 0, 0, 0, 0));
            }).Wait(2500);
        }
        catch { }
        Disconnect();
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
        ? "XMG Control Center läuft auch – dort die Wasserkühlung nicht steuern, sonst überschreiben sich beide"
        : null;
}
