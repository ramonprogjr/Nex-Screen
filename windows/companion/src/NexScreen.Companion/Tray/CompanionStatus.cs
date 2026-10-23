using NexScreen.Companion.Adb;

namespace NexScreen.Companion.Tray;

public enum StatusKind { Waiting, Streaming, NoMonitor, AdbNotFound, Unauthorized }

/// <param name="OutputAvailable">False when the selected virtual monitor is not attached (null: not checked yet).</param>
/// <param name="ClientConnected">A tablet session is open.</param>
/// <param name="Adb">adb link state (Disabled with --no-adb).</param>
/// <param name="Connection">How the linked tablet is connected (USB or wireless debugging).</param>
public readonly record struct StatusSnapshot(bool? OutputAvailable, bool ClientConnected, AdbLinkState Adb,
    AdbConnection Connection = AdbConnection.None)
{
    /// <summary>A tablet is there: linked through adb, or a client session is open (also with --no-adb).</summary>
    public bool TabletPresent => ClientConnected || Adb == AdbLinkState.Linked;
}

public static class StatusText
{
    /// <summary>Most actionable state first: without a monitor nothing streams, even with a tablet connected.</summary>
    public static StatusKind Classify(StatusSnapshot s)
    {
        if (s.OutputAvailable == false)
        {
            return StatusKind.NoMonitor;
        }

        if (s.ClientConnected)
        {
            return StatusKind.Streaming;
        }

        return s.Adb switch
        {
            AdbLinkState.NotFound => StatusKind.AdbNotFound,
            AdbLinkState.Unauthorized => StatusKind.Unauthorized,
            _ => StatusKind.Waiting,
        };
    }

    public static string Text(StatusKind kind) => kind switch
    {
        StatusKind.Streaming => "NexScreen: transmitindo para o tablet",
        StatusKind.NoMonitor => "NexScreen: nenhum monitor virtual em modo Estender",
        StatusKind.AdbNotFound => "NexScreen: adb não encontrado",
        StatusKind.Unauthorized => "Tablet não autorizado — aceite a depuração USB no tablet",
        _ => "NexScreen: aguardando o tablet",
    };

    /// <summary>Second menu line; null when no tablet is linked through adb.</summary>
    public static string? ConnectionText(AdbConnection connection) => connection switch
    {
        AdbConnection.Usb => "Tablet conectado por USB",
        AdbConnection.Wifi => "Tablet conectado por Wi-Fi",
        _ => null,
    };
}

/// <summary>Thread-safe aggregate of pipeline, transport and adb state; raises Changed from any thread.</summary>
public sealed class CompanionStatus
{
    private readonly object _gate = new();
    private StatusSnapshot _snapshot;
    private object? _session;

    public CompanionStatus(bool adbEnabled) =>
        _snapshot = new StatusSnapshot(null, false, adbEnabled ? AdbLinkState.Starting : AdbLinkState.Disabled);

    public event Action<StatusSnapshot>? Changed;

    public StatusSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void SetOutputAvailable(bool available) => Update(s => s with { OutputAvailable = available });

    public void SetAdb(AdbLinkState state, AdbConnection connection) => Update(s => s with { Adb = state, Connection = connection });

    public void SessionStarted(object session)
    {
        lock (_gate)
        {
            _session = session;
        }

        Update(s => s with { ClientConnected = true });
    }

    /// <summary>A replaced session ends after its successor started; only the current one clears the flag.</summary>
    public void SessionEnded(object session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }

            _session = null;
        }

        Update(s => s with { ClientConnected = false });
    }

    private void Update(Func<StatusSnapshot, StatusSnapshot> change)
    {
        StatusSnapshot next;
        lock (_gate)
        {
            next = change(_snapshot);
            if (next == _snapshot)
            {
                return;
            }

            _snapshot = next;
        }

        Changed?.Invoke(next);
    }
}
