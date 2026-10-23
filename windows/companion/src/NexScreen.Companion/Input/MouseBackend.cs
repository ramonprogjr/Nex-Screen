using System.Runtime.InteropServices;
using NexScreen.Companion.Util;

namespace NexScreen.Companion.Input;

/// <summary>MOUSEEVENTF_* flags of MOUSEINPUT.dwFlags.</summary>
[Flags]
public enum MouseFlags : uint
{
    None = 0,
    Move = 0x0001,
    LeftDown = 0x0002,
    LeftUp = 0x0004,
    RightDown = 0x0008,
    RightUp = 0x0010,
    MiddleDown = 0x0020,
    MiddleUp = 0x0040,
    Wheel = 0x0800,
    HWheel = 0x1000,
    VirtualDesk = 0x4000,
    Absolute = 0x8000,
}

/// <summary>One MOUSEINPUT. <see cref="Data"/> is the wheel delta for Wheel/HWheel.</summary>
public readonly record struct MouseInput(int Dx, int Dy, int Data, MouseFlags Flags);

/// <summary>Where injected mouse input goes; faked in tests.</summary>
public interface IMouseBackend
{
    /// <summary>Current virtual screen (all monitors) in physical pixels.</summary>
    DesktopRect VirtualScreen { get; }

    /// <summary>Injects the inputs as one uninterrupted batch. Returns how many were inserted.</summary>
    int Send(ReadOnlySpan<MouseInput> inputs);
}

/// <summary>SendInput on the interactive desktop.</summary>
public sealed partial class Win32MouseBackend : IMouseBackend
{
    private const int InputMouse = 0;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int DpiAwarenessPerMonitorAware = 2;

    private int _failuresLogged;

    public Win32MouseBackend()
    {
        // The manifest declares PerMonitorV2; without it GetSystemMetrics and DXGI rectangles would disagree.
        var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        if (awareness != DpiAwarenessPerMonitorAware)
        {
            Log.Warn($"Process is not per-monitor DPI aware (awareness {awareness}); injected pointer positions may be off on scaled monitors.");
        }
    }

    public DesktopRect VirtualScreen => new(
        GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    public int Send(ReadOnlySpan<MouseInput> inputs)
    {
        if (inputs.IsEmpty)
        {
            return 0;
        }

        Span<INPUT> native = inputs.Length <= 8 ? stackalloc INPUT[inputs.Length] : new INPUT[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            var m = inputs[i];
            native[i] = new INPUT
            {
                Type = InputMouse,
                Mouse = new MOUSEINPUT { Dx = m.Dx, Dy = m.Dy, MouseData = unchecked((uint)m.Data), Flags = (uint)m.Flags },
            };
        }

        uint sent;
        unsafe
        {
            fixed (INPUT* p = native)
            {
                sent = SendInput((uint)native.Length, p, sizeof(INPUT));
            }
        }

        if (sent != inputs.Length && _failuresLogged < 5)
        {
            // Typical causes: the secure desktop (UAC, lock screen) is active, or UIPI blocks input to an elevated window.
            _failuresLogged++;
            Log.Warn($"SendInput inserted {sent} of {inputs.Length} events (error {Marshal.GetLastPInvokeError()}).");
        }

        return (int)sent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // MOUSEINPUT is the largest member of the INPUT union on x64 (40 bytes in total)
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int Type;
        public MOUSEINPUT Mouse;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    private static partial int GetAwarenessFromDpiAwarenessContext(IntPtr value);
}
