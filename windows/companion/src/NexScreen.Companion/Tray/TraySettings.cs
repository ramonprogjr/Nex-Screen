using Microsoft.Win32;

namespace NexScreen.Companion.Tray;

/// <summary>Tray options remembered across restarts, under HKCU\Software\NexScreen (per user, no admin).</summary>
public sealed class TraySettings(string subKey = TraySettings.DefaultSubKey)
{
    public const string DefaultSubKey = @"Software\NexScreen";
    public const string KeepExtendedValue = "KeepExtended";
    public const string VirtualMonitorRectValue = "VirtualMonitorRect";

    /// <summary>--no-auto-extend wins for the run; otherwise the saved choice.</summary>
    public static bool EffectiveKeepExtended(bool commandLineAllows, bool saved) => commandLineAllows && saved;

    /// <summary>"Manter a tela do tablet estendida"; on when never set or unreadable.</summary>
    public bool KeepExtended
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
                return key?.GetValue(KeepExtendedValue) is not int v || v != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }

        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
            key.SetValue(KeepExtendedValue, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>Last desktop rectangle of the extended virtual monitor ("x,y,width,height"), to restore it in place.</summary>
    public Displays.DesktopRect? VirtualMonitorRect
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
                return Displays.DesktopRect.Parse(key?.GetValue(VirtualMonitorRectValue) as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
            if (value is { } r)
            {
                key.SetValue(VirtualMonitorRectValue, r.ToString(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(VirtualMonitorRectValue, throwOnMissingValue: false);
            }
        }
    }
}
