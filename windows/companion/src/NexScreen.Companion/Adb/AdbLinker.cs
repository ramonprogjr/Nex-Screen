using NexScreen.Companion.Util;

namespace NexScreen.Companion.Adb;

public enum AdbLinkState { Disabled, Starting, NotFound, NoDevice, Unauthorized, Linked, Error }

/// <summary>
/// Background tablet link over USB or wireless debugging: polls `adb devices -l` and, for each authorized physical
/// device that appears (or reconnects, i.e. gets a new transport id), runs `adb reverse tcp:port tcp:port` and starts
/// the NexScreen app, on one transport per device. With no device, already-paired tablets announced over mDNS are
/// reconnected (`adb connect`); pairing is never automated. Runs on its own thread; never touches capture.
/// </summary>
public sealed class AdbLinker(string? explicitAdbPath, int port)
{
    public const string AppActivity = "app.nexscreen/.MainActivity";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NotFoundRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReverseCheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MdnsMinInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MdnsMaxInterval = TimeSpan.FromSeconds(60);

    // The first adb call may start the adb server, which takes a few seconds.
    private static readonly TimeSpan DevicesTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private sealed class Linked
    {
        public string? TransportId;
        public bool Done;
        public DateTime NextAttempt;
        public TimeSpan Backoff = TimeSpan.FromSeconds(2);
        public DateTime NextReverseCheck;
        public bool AppWarned;
    }

    private readonly Dictionary<string, Linked> _devices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedNotReady = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Serial, string? TransportId), string> _physicalIds = [];
    private readonly HashSet<string> _reportedConnects = new(StringComparer.Ordinal);
    private Thread? _thread;
    private string? _adb;
    private TimeSpan _errorBackoff = PollInterval;
    private string? _lastError;
    private DateTime _nextMdns;
    private TimeSpan _mdnsInterval = MdnsMinInterval;

    public AdbLinkState State { get; private set; } = AdbLinkState.Starting;

    /// <summary>How the linked tablet is connected (None when nothing is linked).</summary>
    public AdbConnection Connection { get; private set; }

    /// <summary>Raised on the linker thread when State or Connection changes.</summary>
    public event Action<AdbLinkState, AdbConnection>? StateChanged;

    public void Start(CancellationToken ct)
    {
        _thread = new Thread(() => Loop(ct)) { Name = "AdbLink", IsBackground = true };
        _thread.Start();
    }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = Poll();
            }
            catch (Exception ex)
            {
                // e.g. adb.exe deleted or blocked: look it up again later.
                ReportError($"adb failed: {ex.GetType().Name}: {ex.Message.Trim()}");
                _adb = null;
                wait = NextErrorBackoff();
            }

            ct.WaitHandle.WaitOne(wait);
        }
    }

    private TimeSpan Poll()
    {
        if (_adb is null || !File.Exists(_adb))
        {
            _adb = AdbLocator.Find(explicitAdbPath);
            if (_adb is null)
            {
                if (State != AdbLinkState.NotFound)
                {
                    Log.Warn(explicitAdbPath is null
                        ? "adb not found (PATH, Android SDK, WinGet Google.PlatformTools); install it with: winget install --id Google.PlatformTools -e"
                        : $"adb not found at {explicitAdbPath} (--adb).");
                }

                SetState(AdbLinkState.NotFound, AdbConnection.None);
                return NotFoundRetry;
            }

            Log.Info($"Using adb at {_adb}");
        }

        var r = AdbRunner.Run(_adb, DevicesTimeout, "devices", "-l");
        if (!r.Ok)
        {
            ReportError(r.TimedOut ? "adb devices timed out" : $"adb devices failed (exit code {r.ExitCode}): {FirstLine(r.Output)}");
            return NextErrorBackoff();
        }

        _errorBackoff = PollInterval;
        _lastError = null;
        var devices = AdbOutput.ParseDevices(r.Output);
        var now = DateTime.UtcNow;
        ReportNotReady(devices);

        var ready = devices.Where(d => d.IsReady).ToList();
        var chosen = AdbOutput.ChoosePerPhysicalDevice(ready.Select(d => (d, PhysicalId(d))));
        ForgetPhysicalIds(ready);

        foreach (var gone in _devices.Keys.Where(s => !chosen.Any(d => d.Serial == s)).ToList())
        {
            if (ready.Any(d => d.Serial == gone))
            {
                // Same tablet now reachable on a preferred transport (e.g. USB plugged in next to Wi-Fi).
                Log.Info($"Tablet {AdbOutput.MaskSerial(gone)}: switching to another connection.");
                AdbRunner.Run(_adb, CommandTimeout, "-s", gone, "reverse", "--remove", $"tcp:{port}");
            }
            else
            {
                Log.Info($"Tablet {AdbOutput.MaskSerial(gone)} left adb (unplugged, Wi-Fi lost or reconnecting).");
            }

            _devices.Remove(gone);
        }

        foreach (var d in chosen)
        {
            if (!_devices.TryGetValue(d.Serial, out var link))
            {
                link = new Linked { TransportId = d.TransportId };
                _devices[d.Serial] = link;
            }
            else if (d.TransportId is not null && link.TransportId != d.TransportId)
            {
                // Reconnected between two polls: the reverse is gone.
                Log.Info($"Tablet {AdbOutput.MaskSerial(d.Serial)} reconnected.");
                link.TransportId = d.TransportId;
                link.Done = false;
                link.AppWarned = false;
                link.NextAttempt = now;
            }

            if (!link.Done && now >= link.NextAttempt)
            {
                TryLink(d.Serial, link, now);
            }
            else if (link.Done && now >= link.NextReverseCheck)
            {
                CheckReverse(d.Serial, link, now);
            }
        }

        if (ready.Count == 0)
        {
            TryMdnsReconnect(devices, now);
        }
        else
        {
            _mdnsInterval = MdnsMinInterval;
            _nextMdns = now;
        }

        var linked = chosen.FirstOrDefault(d => _devices.TryGetValue(d.Serial, out var l) && l.Done);
        SetState(
            linked is not null ? AdbLinkState.Linked
            : devices.Any(d => d.State is AdbDeviceState.Unauthorized or AdbDeviceState.NoPermissions) ? AdbLinkState.Unauthorized
            : AdbLinkState.NoDevice,
            linked is null ? AdbConnection.None : AdbOutput.ConnectionOf(linked.Serial));
        return PollInterval;
    }

    /// <summary>ro.serialno identifies the physical device behind its USB, ip:port and mDNS transports.</summary>
    private string PhysicalId(AdbDevice d)
    {
        if (_physicalIds.TryGetValue((d.Serial, d.TransportId), out var id))
        {
            return id;
        }

        var r = AdbRunner.Run(_adb!, CommandTimeout, "-s", d.Serial, "shell", "getprop", "ro.serialno");
        var value = r.Ok ? r.Output.Trim() : string.Empty;
        id = value.Length > 0 && !value.Contains(' ') ? value : d.Serial;
        if (r.Ok)
        {
            _physicalIds[(d.Serial, d.TransportId)] = id;
        }

        return id;
    }

    private void ForgetPhysicalIds(List<AdbDevice> ready)
    {
        foreach (var key in _physicalIds.Keys.Where(k => !ready.Any(d => d.Serial == k.Serial && d.TransportId == k.TransportId)).ToList())
        {
            _physicalIds.Remove(key);
        }
    }

    private void ReportNotReady(List<AdbDevice> devices)
    {
        _reportedNotReady.RemoveWhere(s => !devices.Any(d => d.Serial == s && !d.IsReady));
        foreach (var d in devices.Where(d => !d.IsReady))
        {
            if (_reportedNotReady.Add(d.Serial))
            {
                Log.Warn(d.State is AdbDeviceState.Unauthorized or AdbDeviceState.NoPermissions
                    ? $"Device {AdbOutput.MaskSerial(d.Serial)} is {d.RawState}: unlock the tablet and accept \"Allow USB debugging\"."
                    : $"Device {AdbOutput.MaskSerial(d.Serial)} is {d.RawState}; waiting.");
            }
        }
    }

    /// <summary>
    /// The wireless debugging port changes when the tablet toggles it or reboots. Paired tablets announce the new
    /// port over mDNS; `adb connect` only succeeds for a PC the user paired before.
    /// </summary>
    private void TryMdnsReconnect(List<AdbDevice> devices, DateTime now)
    {
        if (now < _nextMdns)
        {
            return;
        }

        var r = AdbRunner.Run(_adb!, CommandTimeout, "mdns", "services");
        var services = r.Ok ? AdbOutput.ParseTlsConnectServices(r.Output) : [];
        var connected = false;
        foreach (var address in services.Where(a => !devices.Any(d => d.Serial == a)))
        {
            var c = AdbRunner.Run(_adb!, CommandTimeout, "connect", address);
            var ok = c.Ok && c.Output.Contains("connected to", StringComparison.OrdinalIgnoreCase) &&
                     !c.Output.Contains("failed", StringComparison.OrdinalIgnoreCase);
            if (_reportedConnects.Add(address + ok))
            {
                Log.Info(ok
                    ? $"Wireless debugging: reconnected to a paired tablet at {AdbOutput.MaskSerial(address)}."
                    : $"Wireless debugging: adb connect {AdbOutput.MaskSerial(address)} failed ({FirstLine(c.Output)}); pair the tablet once with adb pair.");
            }

            connected |= ok;
        }

        _mdnsInterval = connected ? MdnsMinInterval : Min(_mdnsInterval * 2, MdnsMaxInterval);
        _nextMdns = now + (connected ? MdnsMinInterval : _mdnsInterval);
    }

    private void TryLink(string serial, Linked link, DateTime now)
    {
        var masked = AdbOutput.MaskSerial(serial);
        var reverse = AdbRunner.Run(_adb!, CommandTimeout, "-s", serial, "reverse", $"tcp:{port}", $"tcp:{port}");
        if (!reverse.Ok)
        {
            Log.Warn($"adb reverse on {masked} failed ({(reverse.TimedOut ? "timeout" : FirstLine(reverse.Output))}); retrying in {link.Backoff.TotalSeconds:0} s.");
            link.NextAttempt = now + link.Backoff;
            link.Backoff = Min(link.Backoff * 2, MaxBackoff);
            return;
        }

        var start = AdbRunner.Run(_adb!, CommandTimeout, "-s", serial, "shell", "am", "start", "-n", AppActivity);
        if (!start.Ok || AdbOutput.AmStartFailed(start.Output))
        {
            // The reverse is in place; the user can still open the app by hand.
            if (!link.AppWarned)
            {
                Log.Warn($"Could not start the NexScreen app on {masked} ({(start.TimedOut ? "timeout" : FirstLine(start.Output))}). Is it installed?");
                link.AppWarned = true;
            }
        }

        link.Done = true;
        link.Backoff = TimeSpan.FromSeconds(2);
        link.NextReverseCheck = now + ReverseCheckInterval;
        var via = AdbOutput.ConnectionOf(serial) == AdbConnection.Wifi ? "Wi-Fi (wireless debugging)" : "USB";
        Log.Info($"Tablet {masked} linked over {via} (adb reverse tcp:{port}) and the NexScreen app started.");
    }

    private void CheckReverse(string serial, Linked link, DateTime now)
    {
        link.NextReverseCheck = now + ReverseCheckInterval;
        var list = AdbRunner.Run(_adb!, CommandTimeout, "-s", serial, "reverse", "--list");
        if (list.Ok && !AdbOutput.HasReverse(list.Output, port))
        {
            Log.Info($"adb reverse missing on {AdbOutput.MaskSerial(serial)}; restoring.");
            var r = AdbRunner.Run(_adb!, CommandTimeout, "-s", serial, "reverse", $"tcp:{port}", $"tcp:{port}");
            if (!r.Ok)
            {
                link.Done = false;
                link.NextAttempt = now + link.Backoff;
            }
        }
    }

    private void ReportError(string message)
    {
        if (message != _lastError)
        {
            Log.Warn(message);
            _lastError = message;
        }

        SetState(AdbLinkState.Error, AdbConnection.None);
    }

    private TimeSpan NextErrorBackoff()
    {
        var wait = _errorBackoff;
        _errorBackoff = Min(_errorBackoff * 2, MaxBackoff);
        return wait;
    }

    private void SetState(AdbLinkState state, AdbConnection connection)
    {
        if (State == state && Connection == connection)
        {
            return;
        }

        State = state;
        Connection = connection;
        StateChanged?.Invoke(state, connection);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string FirstLine(string s)
    {
        var line = s.Trim().Split('\n').FirstOrDefault(l => !l.TrimStart().StartsWith('*'))?.Trim() ?? string.Empty;
        return line.Length > 160 ? line[..160] : line;
    }
}
