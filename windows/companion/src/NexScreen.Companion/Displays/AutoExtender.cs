using System.Diagnostics;
using NexScreen.Companion.Util;

namespace NexScreen.Companion.Displays;

/// <summary>
/// Rate limit for re-attaching the virtual monitor: at most once per <see cref="MinInterval"/>, and after
/// <see cref="MaxAttempts"/> attempts that left the monitor detached it gives up until the monitor is attached again
/// or a tablet reconnects (<see cref="Reset"/>).
/// </summary>
public sealed class ExtendPolicy
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public const int MaxAttempts = 3;

    private DateTime? _last;
    private int _attempts;

    public bool GaveUp => _attempts >= MaxAttempts;

    public bool ShouldExtend(DateTime now, bool enabled, bool tabletPresent, bool monitorDetached)
    {
        if (!monitorDetached)
        {
            _attempts = 0;
            return false;
        }

        if (!enabled || !tabletPresent || GaveUp || (_last is { } last && now - last < MinInterval))
        {
            return false;
        }

        _last = now;
        _attempts++;
        return true;
    }

    public void Reset() => _attempts = 0;
}

/// <summary>
/// "Manter a tela do tablet estendida": when a tablet is present but the virtual monitor fell out of the desktop
/// (reboot into "PC screen only", Win+P), runs DisplaySwitch.exe /extend. Own thread, polls every 3 s.
/// </summary>
public sealed class AutoExtender(string outputSelector, Func<bool> tabletPresent, Func<bool?> outputAvailable)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly ExtendPolicy _policy = new();
    private readonly object _gate = new();
    private volatile bool _enabled = true;
    private bool _wasPresent;
    private bool _reportedGiveUp;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Log.Info($"Keep the tablet screen extended: {(value ? "on" : "off")}");
            lock (_gate)
            {
                _policy.Reset();
                _reportedGiveUp = false;
            }
        }
    }

    public void Start(CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            while (!ct.WaitHandle.WaitOne(PollInterval))
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Auto-extend check failed: {ex.GetType().Name}: {ex.Message.Trim()}");
                }
            }
        })
        {
            Name = "AutoExtend",
            IsBackground = true,
        };
        thread.Start();
    }

    private void Tick()
    {
        var present = tabletPresent();
        lock (_gate)
        {
            if (present && !_wasPresent)
            {
                // A tablet (re)connected: give the re-attach a fresh chance.
                _policy.Reset();
                _reportedGiveUp = false;
            }

            _wasPresent = present;

            // Cheap gate: only query all display paths while the capture has no output. Then any connected virtual
            // monitor counts, because "Duplicate" mode keeps it active but without an output of its own.
            var detached = outputAvailable() == false && DisplayEnumerator.IsVirtualMonitorConnected(outputSelector);
            if (!_policy.ShouldExtend(DateTime.UtcNow, _enabled, present, detached))
            {
                if (detached && _policy.GaveUp && !_reportedGiveUp && _enabled && present)
                {
                    _reportedGiveUp = true;
                    Log.Warn($"The virtual monitor is still not attached after {ExtendPolicy.MaxAttempts} tries; " +
                             "use Win+P > Extend (or the tray menu).");
                }

                return;
            }
        }

        Log.Info("Virtual monitor is connected but not extended (detached or duplicated) while a tablet is present; running DisplaySwitch.exe /extend.");
        Extend();
    }

    /// <summary>Same as Win+P > Extend.</summary>
    public static void Extend()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "/extend") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Could not run DisplaySwitch.exe: {ex.Message}");
        }
    }
}
