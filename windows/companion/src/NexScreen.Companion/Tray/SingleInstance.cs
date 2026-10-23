namespace NexScreen.Companion.Tray;

/// <summary>One tray instance per user session (Local\ named objects). A second launch pings the first and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\NexScreen.Companion.Tray";
    private const string ShowEventName = @"Local\NexScreen.Companion.Tray.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// <summary>Null when another instance owns the session; that instance is asked to show itself.</summary>
    public static SingleInstance? TryAcquire()
    {
        // Ownership is not needed: the named object existing is the signal, and it dies with the process.
        var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var other))
            {
                using (other)
                {
                    other.Set();
                }
            }

            return null;
        }

        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName));
    }

    /// <summary>Calls <paramref name="onShow"/> (on a thread-pool thread) each time a second launch happens.</summary>
    public void OnShowRequested(Action onShow) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        _mutex.Dispose();
    }
}
