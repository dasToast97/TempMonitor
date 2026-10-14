using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace OasisBridge;

// ===========================================================================
// OasisBridge: verbindet sich per Bluetooth LE mit der XMG OASIS (Uniwill LCT21001 / LCT22002)
// und nimmt Befehle von Toast Temperatur über die Standard-Eingabe entgegen.
//
// Eingabe (eine Zeile je Befehl):  addr <hex|->  ·  volt <0-3>  ·  release <lüfter> <pumpe>
//                                   fan <0-100>   ·  pump <0-100> ·  forget  ·  exit
// Ausgabe:  state <text> · connected <modell>\t<firmware> · disconnected · flow <n> · found <hex> · sent <lüfter> <pumpe>
//
// Gerät: Nordic-UART-Dienst, Rahmen FE <cmd> <an/aus> <p1> <p2> <p3> <p4> EF
//   1B = Lüfter (p1 %), 1C = Pumpe (p1 %, p2 Spannung), 32 = Durchfluss abfragen, 38 = abmelden
// ===========================================================================
static class Program
{
    static readonly Guid Nus = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    static readonly Guid TxId = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    static readonly Guid RxId = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");

    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempMonitor", "oasis.log");

    static readonly object Gate = new();
    static readonly object OutGate = new();

    static ulong? _addr;
    static int _volt;
    static float _fan = -1, _pump = -1, _relFan = 60, _relPump = 100;
    static float _sentFan = -1, _sentPump = -1;
    static int _sentVolt = -1;
    static bool _forget, _exit;
    static DateTime _lastSend = DateTime.MinValue, _lastQuery = DateTime.MinValue;

    static BluetoothLEDevice? _dev;
    static GattCharacteristic? _tx, _rx;
    static TaskCompletionSource<string>? _version;
    static volatile bool _connected;
    static string? _model;
    static int _flow = -1;
    static readonly HashSet<string> _unknown = new();

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

    static void Out(string line)
    {
        lock (OutGate)
        {
            try { Console.Out.WriteLine(line); Console.Out.Flush(); } catch { }
        }
    }

    static void State(string s) { Out("state " + s); }

    static int Main()
    {

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Absturz: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("Hintergrund: " + e.Exception.Message); e.SetObserved(); };
        Log("OasisBridge gestartet");
        var reader = new Thread(ReadInput) { IsBackground = true, Name = "stdin" };
        reader.Start();
        try { Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log("Beendet mit Fehler: " + ex); }
        Log("OasisBridge beendet");
        return 0;
    }

    static void ReadInput()
    {
        try
        {
            string? line;
            while ((line = Console.In.ReadLine()) != null)
            {
                var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                float F(int i) => p.Length > i && float.TryParse(p[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
                lock (Gate)
                {
                    switch (p[0])
                    {
                        case "addr": _addr = p.Length > 1 && ulong.TryParse(p[1], System.Globalization.NumberStyles.HexNumber, null, out var a) ? a : null; break;
                        case "volt": _volt = (int)Math.Clamp(F(1), 0, 3); break;
                        case "release": _relFan = F(1); _relPump = F(2); break;
                        case "fan": _fan = Math.Clamp(F(1), 0, 100); break;
                        case "pump": _pump = Math.Clamp(F(1), 0, 100); break;
                        case "forget": _forget = true; break;
                        case "exit": _exit = true; break;
                    }
                }
            }
        }
        catch { }
        // Toast Temperatur ist weg -> Standardwerte setzen und beenden
        lock (Gate) _exit = true;
    }

    static async Task Run()
    {
        int misses = 0;
        await Task.Delay(300);   // erste Befehle (Adresse, Spannung …) abwarten
        while (true)
        {
            bool exit, forget;
            lock (Gate) { exit = _exit; forget = _forget; _forget = false; }
            if (exit) { await Shutdown(); return; }
            if (forget) { lock (Gate) _addr = null; Disconnect(); Out("disconnected"); }

            try
            {
                if (!_connected)
                {
                    ulong? addr;
                    lock (Gate) addr = _addr;
                    if (addr == null)
                    {
                        State("Suche XMG Oasis …");
                        addr = await Find();
                        if (addr == null)
                        {
                            misses++;
                            State("Nicht gefunden – Oasis eingeschaltet, Bluetooth am Laptop an?");
                            await Wait(misses < 3 ? 20_000 : 120_000);
                            continue;
                        }
                        lock (Gate) _addr = addr;
                        Out($"found {addr:X12}");
                    }
                    State("Verbinde …");
                    await Connect(addr.Value);
                    misses = 0;
                }

                await SendPending();
                if ((DateTime.Now - _lastQuery).TotalSeconds >= 10)
                {
                    _lastQuery = DateTime.Now;
                    await Write(Frame(0x32, 0, 0, 0, 0, 0));
                }
                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                Log("Fehler: " + ex.Message);
                State("Verbindung verloren – verbinde neu … (" + ex.Message + ")");
                Disconnect();
                Out("disconnected");
                await Wait(8000);
            }
        }
    }

    /// Warten, aber bei "exit" sofort aufhören
    static async Task Wait(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            lock (Gate) if (_exit || _forget) return;
            await Task.Delay(200);
        }
    }

    static async Task<ulong?> Find()
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
                    _model = d.Name;
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
                    _model = name;
                    Log($"Gefunden: {name} {a.BluetoothAddress:X12} ({a.RawSignalStrengthInDBm} dBm)");
                    tcs.TrySetResult(a.BluetoothAddress);
                }
            }
            catch { }
        };
        w.Stopped += (_, a) =>
        {
            try { if (a.Error != BluetoothError.Success) { Log("Suche gestoppt: " + a.Error); tcs.TrySetResult(null); } } catch { }
        };
        w.Start();
        var done = await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        try { w.Stop(); } catch { }
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    static async Task Connect(ulong addr)
    {
        Disconnect();
        var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr) ?? throw new IOException("Gerät nicht erreichbar");
        _dev = dev;
        _model ??= dev.Name;
        var sr = await dev.GetGattServicesForUuidAsync(Nus, BluetoothCacheMode.Uncached);
        if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0)
            throw new IOException("Steuerdienst nicht gefunden (" + sr.Status + ")");
        var svc = sr.Services[0];
        _tx = (await svc.GetCharacteristicsForUuidAsync(TxId)).Characteristics.FirstOrDefault() ?? throw new IOException("Sende-Kanal fehlt");
        _rx = (await svc.GetCharacteristicsForUuidAsync(RxId)).Characteristics.FirstOrDefault() ?? throw new IOException("Empfangs-Kanal fehlt");
        _rx.ValueChanged += OnNotify;
        try
        {
            var st = await _rx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (st != GattCommunicationStatus.Success) Log("Benachrichtigungen nicht möglich (" + st + ") – steuere ohne Rückmeldung");
        }
        catch (Exception ex) { Log("Benachrichtigungen: " + ex.Message); }

        _version = new TaskCompletionSource<string>();
        await Write(new byte[] { 0x73, 0x77 });   // "sw" -> Firmware-Text
        var done = await Task.WhenAny(_version.Task, Task.Delay(4000));
        string fw = done == _version.Task ? _version.Task.Result : "";
        if (fw.Length == 0) Log("Keine Antwort auf den Handschlag – steuere trotzdem");

        dev.ConnectionStatusChanged += (d, _) =>
        {
            try
            {
                if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _connected)
                {
                    Log("Verbindung getrennt");
                    _connected = false;
                    Out("disconnected");
                    State("Getrennt – verbinde neu …");
                }
            }
            catch { }
        };
        _sentFan = _sentPump = -1;
        _sentVolt = -1;
        _connected = true;
        string fwShort = fw.Length > 17 ? fw[17..].Trim() : fw;
        Out($"connected {_model ?? "XMG Oasis"}\t{fwShort}");
        State("Verbunden");
        Log($"Verbunden mit {_model} ({addr:X12}), Firmware {fwShort}");
    }

    static void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var r = DataReader.FromBuffer(args.CharacteristicValue);
            var b = new byte[r.UnconsumedBufferLength];
            r.ReadBytes(b);
            if (b.Length >= 8 && b[0] == 0xFE && (b[1] == 0x31 || b[1] == 0x32))
            {
                if (_flow != b[3]) Log($"Durchfluss-Status {b[3]}");
                _flow = b[3];
                Out($"flow {b[3]}");
            }
            else if (b.Length > 0 && b[0] != 0xFE && b[0] != 0xFA && b[0] >= 0x20)
            {
                _version?.TrySetResult(Encoding.UTF8.GetString(b).Trim('\0', ' ', '\r', '\n'));
            }
            else
            {
                // Unbekannter Rahmen (z. B. erweiterter 30-Byte-Rahmen der Mk2) – zur Auswertung protokollieren
                string hex = Convert.ToHexString(b);
                if (_unknown.Add(hex.Length > 16 ? hex[..16] : hex) && _unknown.Count <= 40)
                    Log($"Rahmen: {hex}");
                // Erweiterter Rahmen FA <cmd> … AF: Durchfluss wie im kurzen Rahmen an Stelle 3
                if (b.Length >= 8 && b[0] == 0xFA && (b[1] == 0x31 || b[1] == 0x32))
                {
                    _flow = b[3];
                    Out($"flow {b[3]}");
                }
            }
        }
        catch { }
    }

    static byte[] Frame(byte cmd, byte on, byte p1, byte p2, byte p3, byte p4) => new byte[] { 0xFE, cmd, on, p1, p2, p3, p4, 0xEF };

    static async Task Write(byte[] data)
    {
        var tx = _tx ?? throw new IOException("Nicht verbunden");
        var w = new DataWriter();
        w.WriteBytes(data);
        var opt = tx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        var res = await tx.WriteValueWithResultAsync(w.DetachBuffer(), opt);
        if (res.Status != GattCommunicationStatus.Success) throw new IOException("Senden fehlgeschlagen (" + res.Status + ")");
    }

    static async Task SendPending(bool force = false)
    {
        float fan, pump;
        int volt;
        lock (Gate) { fan = _fan; pump = _pump; volt = _volt; }
        // alle 5 s erneut senden – so setzt sich Toast Temperatur auch gegen das Control Center durch
        bool keepAlive = force || (DateTime.Now - _lastSend).TotalSeconds >= 5;
        bool sent = false;
        if (fan >= 0 && (Math.Abs(fan - _sentFan) >= 1 || keepAlive))
        {
            byte d = (byte)Math.Round(fan);
            await Write(Frame(0x1B, (byte)(d > 0 ? 1 : 0), d, 0, 0, 0));
            _sentFan = fan;
            sent = true;
        }
        if (pump >= 0 && (Math.Abs(pump - _sentPump) >= 1 || volt != _sentVolt || keepAlive))
        {
            byte d = (byte)Math.Round(Math.Max(pump, 1));   // Pumpe nie ausschalten
            await Write(Frame(0x1C, 1, d, (byte)volt, 0, 0));
            _sentPump = pump;
            _sentVolt = volt;
            sent = true;
        }
        if (sent)
        {
            _lastSend = DateTime.Now;
            Out(FormattableString.Invariant($"sent {_sentFan:0} {_sentPump:0}"));
        }
    }

    static async Task Shutdown()
    {
        try
        {
            if (_connected)
            {
                lock (Gate) { _fan = _relFan; _pump = _relPump; }
                await SendPending(force: true);
                await Write(Frame(0x38, 0, 0, 0, 0, 0));
                Log("Standardwerte gesendet und abgemeldet");
            }
        }
        catch (Exception ex) { Log("Beim Beenden: " + ex.Message); }
        Disconnect();
    }

    static void Disconnect()
    {
        _connected = false;
        try { if (_rx != null) _rx.ValueChanged -= OnNotify; } catch { }
        _tx = _rx = null;
        try { _dev?.Dispose(); } catch { }
        _dev = null;
    }
}
