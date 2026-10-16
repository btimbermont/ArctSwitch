using HidSharp;

namespace ArctSwitch;

public enum HeadsetState { Unknown, On, Off }

/// <summary>
/// Polls the Arctis 7 wireless dongle over HID to find out whether the headset itself is powered on.
/// Protocol (community reverse-engineered, see HeadsetControl / arctis-rs):
///   write 06 14 -> reply byte[2]: 0x03 = headset connected, 0x01 = receiver only
///   write 06 18 -> reply byte[2]: battery percentage
/// </summary>
public sealed class HeadsetMonitor : IDisposable
{
    public const int VendorId = 0x1038;
    // Arctis 7 (2017), Arctis 7 (2019), Arctis Pro Wireless 2019 share this protocol.
    public static readonly int[] ProductIds = { 0x12AD, 0x1260, 0x1252 };

    public const int MinPollIntervalMs = 500;
    public const int DefaultPollIntervalMs = 1500;
    const int ReadTimeoutMs = 500;
    const int DebounceReadings = 2;

    readonly object _lock = new();
    HidDevice? _device;
    HidStream? _stream;
    Thread? _thread;
    volatile bool _running;
    readonly AutoResetEvent _wake = new(false);

    HeadsetState _candidate = HeadsetState.Unknown;
    int _candidateCount;

    volatile int _pollIntervalMs = DefaultPollIntervalMs;

    public int PollIntervalMs
    {
        get => _pollIntervalMs;
        set
        {
            _pollIntervalMs = Math.Max(MinPollIntervalMs, value);
            _wake.Set(); // apply the new interval right away instead of after the current wait
        }
    }

    public HeadsetState State { get; private set; } = HeadsetState.Unknown;
    public int? Battery { get; private set; }
    public bool DongleFound => _device != null;

    /// <summary>Raised on a background thread when the debounced state changes.</summary>
    public event Action<HeadsetState>? StateChanged;
    /// <summary>Raised on a background thread after every poll (for tooltip/battery updates).</summary>
    public event Action? Polled;

    public void Start()
    {
        if (_running) return;
        _running = true;
        DeviceList.Local.Changed += OnDeviceListChanged;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HeadsetMonitor" };
        _thread.Start();
    }

    void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        lock (_lock) CloseStream();
        _wake.Set();
    }

    void Loop()
    {
        while (_running)
        {
            HeadsetState reading;
            try
            {
                reading = Poll();
            }
            catch (Exception ex)
            {
                Log.Write($"Poll error: {ex.Message}");
                lock (_lock) CloseStream();
                reading = HeadsetState.Off;
            }

            if (reading == _candidate) _candidateCount++;
            else { _candidate = reading; _candidateCount = 1; }

            if (_candidateCount >= DebounceReadings && State != _candidate)
            {
                State = _candidate;
                Log.Write($"Headset state -> {State} (battery {Battery?.ToString() ?? "n/a"})");
                StateChanged?.Invoke(State);
            }
            Polled?.Invoke();
            _wake.WaitOne(_pollIntervalMs);
        }
    }

    HeadsetState Poll()
    {
        lock (_lock)
        {
            if (_stream == null && !TryOpen())
            {
                Battery = null;
                return HeadsetState.Off; // dongle not plugged in
            }

            var status = Query(_stream!, 0x14);
            if (status == null)
            {
                CloseStream();
                Battery = null;
                return HeadsetState.Off;
            }

            if (status[2] == 0x03)
            {
                var bat = Query(_stream!, 0x18);
                Battery = bat == null ? null : Math.Min((int)bat[2], 100);
                return HeadsetState.On;
            }

            Battery = null;
            return HeadsetState.Off;
        }
    }

    bool TryOpen()
    {
        foreach (var dev in CandidateDevices())
        {
            if (!dev.TryOpen(out HidStream stream)) continue;
            stream.ReadTimeout = ReadTimeoutMs;
            stream.WriteTimeout = ReadTimeoutMs;
            try
            {
                if (Query(stream, 0x14) != null)
                {
                    _device = dev;
                    _stream = stream;
                    Log.Write($"Opened dongle HID interface {dev.DevicePath}");
                    return true;
                }
            }
            catch { /* this collection does not accept the command; try the next */ }
            stream.Dispose();
        }
        _device = null;
        return false;
    }

    static IEnumerable<HidDevice> CandidateDevices() =>
        DeviceList.Local.GetHidDevices(VendorId)
            .Where(d => ProductIds.Contains(d.ProductID))
            // The command interface is MI_05 on the 2019 dongle; try it first but don't depend on it.
            .OrderBy(d => d.DevicePath.Contains("mi_05", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    /// <summary>Sends 06 &lt;cmd&gt; and returns the matching 06 &lt;cmd&gt; reply, or null.</summary>
    static byte[]? Query(HidStream stream, byte cmd)
    {
        int outLen = stream.Device.GetMaxOutputReportLength();
        int inLen = stream.Device.GetMaxInputReportLength();
        if (outLen < 2 || inLen < 3) return null;

        var request = new byte[outLen];
        request[0] = 0x06;
        request[1] = cmd;
        stream.Write(request);

        var buffer = new byte[inLen];
        var deadline = Environment.TickCount64 + ReadTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int n;
            try { n = stream.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { return null; }
            // Skip unsolicited reports (e.g. ChatMix dial) until we see our reply.
            if (n >= 3 && buffer[0] == 0x06 && buffer[1] == cmd) return buffer;
        }
        return null;
    }

    void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
        _device = null;
    }

    public void Dispose()
    {
        _running = false;
        DeviceList.Local.Changed -= OnDeviceListChanged;
        _wake.Set();
        _thread?.Join(2000);
        lock (_lock) CloseStream();
    }

    /// <summary>Diagnostic mode: dumps every candidate HID collection and its replies.</summary>
    public static void Probe(TextWriter o, int seconds)
    {
        var devs = DeviceList.Local.GetHidDevices(VendorId).Where(d => ProductIds.Contains(d.ProductID)).ToList();
        o.WriteLine($"Found {devs.Count} SteelSeries Arctis HID collection(s).");
        foreach (var d in devs)
            o.WriteLine($"  {d.DevicePath}  in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()} feat={d.GetMaxFeatureReportLength()}");

        var end = DateTime.Now.AddSeconds(seconds);
        o.WriteLine($"Polling for {seconds}s - switch the headset on and off now.");
        while (DateTime.Now < end)
        {
            foreach (var d in devs)
            {
                if (d.GetMaxOutputReportLength() < 2) continue;
                if (!d.TryOpen(out HidStream s)) { o.WriteLine($"  cannot open {Short(d)}"); continue; }
                using (s)
                {
                    s.ReadTimeout = ReadTimeoutMs;
                    s.WriteTimeout = ReadTimeoutMs;
                    try
                    {
                        var st = Query(s, 0x14);
                        var bat = Query(s, 0x18);
                        o.WriteLine($"{DateTime.Now:HH:mm:ss} {Short(d)}  status={Hex(st)}  battery={Hex(bat)}");
                    }
                    catch (Exception ex) { o.WriteLine($"  {Short(d)} error: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
            Thread.Sleep(1000);
        }
    }

    static string Short(HidDevice d)
    {
        var p = d.DevicePath;
        int i = p.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? p.Substring(i, Math.Min(12, p.Length - i)) : p;
    }

    static string Hex(byte[]? b) => b == null ? "no reply" : BitConverter.ToString(b, 0, Math.Min(8, b.Length));
}
