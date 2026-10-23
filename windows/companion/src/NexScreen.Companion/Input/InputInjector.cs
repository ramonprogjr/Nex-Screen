using NexScreen.Companion.Net;
using NexScreen.Companion.Util;

namespace NexScreen.Companion.Input;

/// <summary>
/// Turns NexLink POINTER/SCROLL into mouse input on the captured output: an absolute move to the mapped pixel
/// followed by the button transitions between the held state and the message's button state, sent as one
/// SendInput batch. Tracks the buttons it pressed so they can be released on cancel or disconnect. Thread-safe.
/// </summary>
public sealed class InputInjector(IMouseBackend backend, Func<DesktopRect?> outputRect)
{
    private static readonly (PointerButtons Button, MouseFlags Down, MouseFlags Up)[] ButtonFlags =
    [
        (PointerButtons.Left, MouseFlags.LeftDown, MouseFlags.LeftUp),
        (PointerButtons.Right, MouseFlags.RightDown, MouseFlags.RightUp),
        (PointerButtons.Middle, MouseFlags.MiddleDown, MouseFlags.MiddleUp),
    ];

    private readonly object _gate = new();
    private PointerButtons _held;
    private bool _warnedNoOutput;

    /// <summary>Buttons currently held down by injected input.</summary>
    public PointerButtons HeldButtons
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    public void OnPointer(PointerMessage pointer)
    {
        lock (_gate)
        {
            if (pointer.Action == PointerAction.Cancel)
            {
                ReleaseLocked("cancel");
                return;
            }

            var output = outputRect();
            if (output is not { IsEmpty: false } rect)
            {
                if (!_warnedNoOutput)
                {
                    _warnedNoOutput = true;
                    Log.Warn("Input ignored: the captured output's position is unknown.");
                }

                // Never leave a button stuck because the output went away mid-drag.
                ReleaseLocked("output unavailable");
                return;
            }

            _warnedNoOutput = false;
            var (x, y) = InputMapping.NormalizedToDesktop(pointer.X, pointer.Y, rect);
            var (dx, dy) = InputMapping.DesktopToAbsolute(x, y, backend.VirtualScreen);

            Span<MouseInput> batch = stackalloc MouseInput[1 + ButtonFlags.Length];
            var n = 0;
            batch[n++] = new MouseInput(dx, dy, 0, MouseFlags.Move | MouseFlags.Absolute | MouseFlags.VirtualDesk);
            var target = pointer.Buttons & NexLink.KnownButtons;
            foreach (var (button, down, up) in ButtonFlags)
            {
                var was = (_held & button) != 0;
                var now = (target & button) != 0;
                if (was != now)
                {
                    batch[n++] = new MouseInput(0, 0, 0, now ? down : up);
                }
            }

            backend.Send(batch[..n]);
            if (pointer.Action != PointerAction.Move || target != _held)
            {
                Log.Debug($"Input {pointer.Action} buttons {target} at norm ({pointer.X},{pointer.Y}) -> desktop ({x},{y}) abs ({dx},{dy})");
            }

            _held = target;
        }
    }

    public void OnScroll(ScrollMessage scroll)
    {
        Span<MouseInput> batch = stackalloc MouseInput[2];
        var n = 0;
        if (scroll.Dy != 0)
        {
            batch[n++] = new MouseInput(0, 0, scroll.Dy, MouseFlags.Wheel);
        }

        if (scroll.Dx != 0)
        {
            batch[n++] = new MouseInput(0, 0, scroll.Dx, MouseFlags.HWheel);
        }

        if (n == 0)
        {
            return;
        }

        lock (_gate)
        {
            backend.Send(batch[..n]);
        }

        Log.Debug($"Input scroll dx {scroll.Dx} dy {scroll.Dy}");
    }

    /// <summary>Releases every button still held by injected input (disconnect, session replaced).</summary>
    public void ReleaseAll(string reason)
    {
        lock (_gate)
        {
            ReleaseLocked(reason);
        }
    }

    private void ReleaseLocked(string reason)
    {
        if (_held == PointerButtons.None)
        {
            return;
        }

        Span<MouseInput> batch = stackalloc MouseInput[ButtonFlags.Length];
        var n = 0;
        foreach (var (button, _, up) in ButtonFlags)
        {
            if ((_held & button) != 0)
            {
                batch[n++] = new MouseInput(0, 0, 0, up);
            }
        }

        backend.Send(batch[..n]);
        Log.Info($"Released held mouse buttons {_held} ({reason}).");
        _held = PointerButtons.None;
    }
}
