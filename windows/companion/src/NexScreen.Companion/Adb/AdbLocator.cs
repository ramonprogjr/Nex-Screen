namespace NexScreen.Companion.Adb;

/// <summary>
/// Finds adb.exe: --adb, the platform-tools folder next to the companion (installer), PATH, the Android SDK, then the
/// WinGet Google.PlatformTools package.
/// </summary>
public static class AdbLocator
{
    public const string ExeName = "adb.exe";

    public static string? Find(string? explicitPath) =>
        Find(explicitPath, Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), File.Exists, SafeGetDirectories,
            AppContext.BaseDirectory);

    /// <summary>Candidates in search order (the first existing one wins).</summary>
    public static IEnumerable<string> Candidates(string? explicitPath, string? pathVariable, string? localAppData,
        Func<string, string, IEnumerable<string>> getDirectories, string? appDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // An explicit path is the only candidate: a typo should not silently pick another adb.
            var p = explicitPath.Trim().Trim('"');
            yield return Directory.Exists(p) ? Path.Combine(p, ExeName) : p;
            yield break;
        }

        // Bundled by the installer; also found when started without --adb (the "Iniciar com o Windows" Run value).
        if (!string.IsNullOrEmpty(appDirectory) && Path.IsPathFullyQualified(appDirectory))
        {
            yield return Path.Combine(appDirectory, "platform-tools", ExeName);
        }

        foreach (var dir in (pathVariable ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var d = dir.Trim('"');
            if (d.Length > 0 && Path.IsPathFullyQualified(d))
            {
                yield return Path.Combine(d, ExeName);
            }
        }

        if (string.IsNullOrEmpty(localAppData))
        {
            yield break;
        }

        yield return Path.Combine(localAppData, "Android", "Sdk", "platform-tools", ExeName);

        var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        foreach (var pkg in getDirectories(packages, "*PlatformTools*").Order(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(pkg, "platform-tools", ExeName);
        }
    }

    public static string? Find(string? explicitPath, string? pathVariable, string? localAppData,
        Func<string, bool> fileExists, Func<string, string, IEnumerable<string>> getDirectories, string? appDirectory = null) =>
        Candidates(explicitPath, pathVariable, localAppData, getDirectories, appDirectory).FirstOrDefault(fileExists);

    private static IEnumerable<string> SafeGetDirectories(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
