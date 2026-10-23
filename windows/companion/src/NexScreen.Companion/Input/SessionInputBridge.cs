using NexScreen.Companion.Net;
using NexScreen.Companion.Util;

namespace NexScreen.Companion.Input;

/// <summary>
/// Feeds POINTER/SCROLL of the active session to the <see cref="InputInjector"/>. Input is dropped until
/// <paramref name="canInject"/> says the session has been told the video format (it cannot know what it touches
/// before), and buttons are released when the session ends or is replaced.
/// </summary>
public sealed class SessionInputBridge
{
    private readonly InputInjector _injector;
    private readonly Func<IDisplaySession, bool> _canInject;
    private readonly object _gate = new();
    private IDisplaySession? _active;
    private long _ignored;

    public SessionInputBridge(IDisplayTransport transport, InputInjector injector, Func<IDisplaySession, bool> canInject)
    {
        _injector = injector;
        _canInject = canInject;
        transport.SessionStarted += OnSessionStarted;
        transport.SessionEnded += OnSessionEnded;
    }

    private void OnSessionStarted(IDisplaySession session)
    {
        lock (_gate)
        {
            _active = session;
            _ignored = 0;
        }

        _injector.ReleaseAll($"client #{session.Id} connected");
        session.PointerReceived += pointer => Handle(session, () => _injector.OnPointer(pointer));
        session.ScrollReceived += scroll => Handle(session, () => _injector.OnScroll(scroll));
    }

    private void OnSessionEnded(IDisplaySession session)
    {
        lock (_gate)
        {
            if (_active != session)
            {
                return; // already replaced; the new session released the buttons when it started
            }

            _active = null;
        }

        _injector.ReleaseAll($"client #{session.Id} disconnected");
    }

    private void Handle(IDisplaySession session, Action inject)
    {
        lock (_gate)
        {
            if (_active != session)
            {
                return;
            }
        }

        if (!_canInject(session))
        {
            if (Interlocked.Increment(ref _ignored) == 1)
            {
                Log.Info($"Client #{session.Id}: input ignored until the video format has been sent.");
            }

            return;
        }

        try
        {
            inject();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let an injection problem tear down the session's receive loop.
            Log.Warn($"Input injection failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
