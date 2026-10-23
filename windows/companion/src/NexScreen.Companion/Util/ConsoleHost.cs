using System.Runtime.InteropServices;

namespace NexScreen.Companion.Util;

/// <summary>
/// The exe is a GUI-subsystem app so --tray never flashes a console window. For the console modes it attaches to
/// the console of the process that launched it (cmd, PowerShell, Windows Terminal), or opens a new one when started
/// from Explorer, so output and Ctrl+C work as with a console app. Redirected output (pipe or file) is used as is.
/// Shells do not wait for GUI-subsystem programs: tools\run-companion.ps1 waits explicitly; from cmd use start /wait.
/// </summary>
internal static partial class ConsoleHost
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const uint FileTypeDisk = 1;
    private const uint FileTypePipe = 3;

    /// <summary>True when Console output goes somewhere (a console, a pipe or a file).</summary>
    public static bool HasOutput { get; private set; }

    /// <summary>True when the process is attached to a console window (Ctrl+C reaches it).</summary>
    public static bool HasConsole { get; private set; }

    public static void Setup(bool allocateIfNone)
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) is FileTypeDisk or FileTypePipe)
        {
            // Still join the parent console (if any) so Ctrl+C reaches us; the inherited std handles are kept.
            HasConsole = AttachConsole(AttachParentProcess);
            HasOutput = true;
            var utf8 = new System.Text.UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            return;
        }

        if (AttachConsole(AttachParentProcess) || (allocateIfNone && AllocConsole()))
        {
            HasOutput = true;
            HasConsole = true;
            // UTF-16 makes .NET use WriteConsoleW: independent of (and not changing) the shared console code page,
            // which the parent shell may reset while we are still writing.
            try
            {
                Console.OutputEncoding = System.Text.Encoding.Unicode;
            }
            catch (IOException)
            {
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int stdHandle);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(IntPtr handle);
}
