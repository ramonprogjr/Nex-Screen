using Microsoft.Win32;

namespace NexScreen.Companion.Tray;

/// <summary>
/// "Iniciar com o Windows": a value under HKCU\...\Run (per user, no admin). Only written when the user ticks the
/// menu item; never enabled automatically.
/// </summary>
public sealed class StartupRegistration(string subKey = StartupRegistration.RunSubKey, string valueName = StartupRegistration.DefaultValueName)
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "NexScreen";

    public static string CommandLine(string exePath) => $"\"{exePath.Trim('"')}\" --tray";

    public string? CurrentValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public bool IsEnabled(string exePath) =>
        string.Equals(CurrentValue(), CommandLine(exePath), StringComparison.OrdinalIgnoreCase);

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(valueName, CommandLine(exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
