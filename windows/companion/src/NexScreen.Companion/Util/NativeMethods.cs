using System.Runtime.InteropServices;

namespace NexScreen.Companion.Util;

internal static partial class NativeMethods
{
    [LibraryImport("winmm.dll")]
    internal static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll")]
    internal static partial uint timeEndPeriod(uint uPeriod);

    private const uint DesktopSwitchAccess = 0x0100; // DESKTOP_SWITCHDESKTOP
    private const uint GenericAll = 0x10000000;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadDesktop(IntPtr hDesktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr hDesktop);

    /// <summary>
    /// Attaches the calling thread to the current input desktop (needed after a desktop switch).
    /// Fails for the secure desktop (UAC / lock screen) when not running as SYSTEM, which is expected.
    /// </summary>
    [ThreadStatic]
    private static IntPtr _attachedDesktop;

    internal static bool TryAttachToInputDesktop()
    {
        var desk = OpenInputDesktop(0, false, GenericAll | DesktopSwitchAccess);
        if (desk == IntPtr.Zero)
        {
            return false;
        }

        if (!SetThreadDesktop(desk))
        {
            CloseDesktop(desk);
            return false;
        }

        // The previous handle is no longer assigned to this thread and can be closed now.
        var previous = _attachedDesktop;
        _attachedDesktop = desk;
        if (previous != IntPtr.Zero)
        {
            CloseDesktop(previous);
        }

        return true;
    }
}
