using NexScreen.Companion.Displays;
using NexScreen.Companion.Util;

namespace NexScreen.Companion.Input;

/// <summary>
/// Desktop rectangle of the output being captured (DXGI OutputDescription.DesktopCoordinates), resolved with the
/// same selector and <see cref="DisplayEnumerator.Find"/> call the capture pipeline uses, so the capture code does
/// not have to expose it. Enumeration costs a few milliseconds, so the value is cached and refreshed in the
/// background when older than <paramref name="maxAge"/>; only the very first lookup blocks.
/// </summary>
public sealed class CapturedOutputLocator(string selector, TimeSpan maxAge)
{
    private readonly object _gate = new();
    private DesktopRect? _rect;
    private double _resolvedAt = double.NegativeInfinity;
    private bool _refreshing;
    private bool _resolvedOnce;

    public CapturedOutputLocator(string selector)
        : this(selector, TimeSpan.FromSeconds(2))
    {
    }

    /// <summary>The captured output's rectangle, or null when the output is not found.</summary>
    public DesktopRect? GetCurrent()
    {
        lock (_gate)
        {
            if (_resolvedOnce)
            {
                if (MonoClock.NowSeconds - _resolvedAt >= maxAge.TotalSeconds && !_refreshing)
                {
                    _refreshing = true;
                    _ = Task.Run(Refresh);
                }

                return _rect;
            }
        }

        Refresh();
        lock (_gate)
        {
            return _rect;
        }
    }

    /// <summary>Re-resolves the output now.</summary>
    public void Refresh()
    {
        DesktopRect? rect = null;
        try
        {
            var info = DisplayEnumerator.Find(selector);
            if (info is not null)
            {
                rect = new DesktopRect(info.Left, info.Top, info.Width, info.Height);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not locate output \"{selector}\" for input: {ex.Message}");
        }

        lock (_gate)
        {
            if (rect != _rect || !_resolvedOnce)
            {
                Log.Info(rect is { } r ? $"Input target: output \"{selector}\" at {r}" : $"Input target: output \"{selector}\" not found");
            }

            _rect = rect;
            _resolvedAt = MonoClock.NowSeconds;
            _resolvedOnce = true;
            _refreshing = false;
        }
    }
}
