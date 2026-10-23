using System.Diagnostics;

namespace NexScreen.Companion.Adb;

public sealed record AdbResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Ok => !TimedOut && ExitCode == 0;
}

/// <summary>Runs adb without a window, with a timeout, capturing stdout + stderr.</summary>
public static class AdbRunner
{
    public static AdbResult Run(string adb, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(adb)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("adb did not start.");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try
            {
                // Not the whole tree: adb may have just spawned the shared adb server.
                p.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            return new AdbResult(-1, string.Empty, true);
        }

        // A freshly started adb server can inherit the pipes; do not wait for it to close them.
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2));
        var output = (stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty) +
                     (stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty);
        return new AdbResult(p.ExitCode, output, false);
    }
}
