using Vortice.DXGI;

namespace NexScreen.Companion.Displays;

public sealed record DisplayOutputInfo(
    string GdiName,
    string FriendlyName,
    string? EdidManufacturer,
    int Width,
    int Height,
    double RefreshHz,
    int Left,
    int Top,
    bool IsPrimary,
    string Rotation,
    int AdapterIndex,
    int OutputIndex,
    string AdapterName,
    long AdapterLuid,
    uint VendorId)
{
    public bool IsNexScreen => DisplayEnumerator.IsNexScreenName(FriendlyName, EdidManufacturer);

    /// <summary>Monitor of the signed Virtual Display Driver (MttVDD) used until the NexScreen driver is signed.</summary>
    public bool IsVdd => DisplayEnumerator.IsVddName(FriendlyName, EdidManufacturer);
}

public static class DisplayEnumerator
{
    public const string NexScreenMonitorName = "NexScreen";
    public const string NexScreenManufacturer = "NXS";
    public const string VddManufacturer = "MTT";
    public const string VddMonitorNamePrefix = "VDD by MTT";

    /// <summary>Lists DXGI outputs attached to the desktop, enriched with DisplayConfig monitor names.</summary>
    public static List<DisplayOutputInfo> Enumerate()
    {
        var targets = new Dictionary<string, DisplayConfigInterop.TargetInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in DisplayConfigInterop.QueryActiveTargets())
            {
                targets.TryAdd(t.GdiDeviceName, t);
            }
        }
        catch (InvalidOperationException ex)
        {
            Util.Log.Warn($"DisplayConfig query failed, monitor names unavailable: {ex.Message}");
        }

        var result = new List<DisplayOutputInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var ad = adapter.Description1;
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var od = output.Description;
                        if (!od.AttachedToDesktop)
                        {
                            continue;
                        }

                        var rect = od.DesktopCoordinates;
                        targets.TryGetValue(od.DeviceName, out var target);
                        result.Add(new DisplayOutputInfo(
                            od.DeviceName,
                            target?.FriendlyName ?? string.Empty,
                            target?.EdidManufacturer,
                            rect.Right - rect.Left,
                            rect.Bottom - rect.Top,
                            target?.RefreshHz ?? 0,
                            rect.Left,
                            rect.Top,
                            rect.Left == 0 && rect.Top == 0,
                            od.Rotation.ToString(),
                            (int)a,
                            (int)o,
                            ad.Description.TrimEnd('\0'),
                            ((long)ad.Luid.HighPart << 32) | ad.Luid.LowPart,
                            ad.VendorId));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True when a virtual monitor the selector would pick (auto / nexscreen / vdd) is connected but not attached to
    /// the desktop, e.g. after a reboot into "PC screen only". Other selectors never trigger a re-attach.
    /// </summary>
    internal static bool IsVirtualMonitorDetached(string selector, IEnumerable<DisplayConfigInterop.MonitorTarget> targets)
    {
        Func<DisplayConfigInterop.MonitorTarget, bool>? matches = selector.ToLowerInvariant() switch
        {
            "auto" => t => IsNexScreenName(t.FriendlyName, t.EdidManufacturer) || IsVddName(t.FriendlyName, t.EdidManufacturer),
            "nexscreen" => t => IsNexScreenName(t.FriendlyName, t.EdidManufacturer),
            "vdd" => t => IsVddName(t.FriendlyName, t.EdidManufacturer),
            _ => null,
        };
        if (matches is null)
        {
            return false;
        }

        var known = targets.Where(matches).ToList();
        return known.Count > 0 && !known.Any(t => t.Active);
    }

    /// <summary>
    /// True when a virtual monitor the selector would pick is connected, attached or not. Used when the capture can't
    /// find its output: in "Duplicate" mode (e.g. after plugging into a USB-C/DisplayPort port) the virtual target is
    /// still active but mirrors the main screen, so it has no output of its own and only /extend brings it back.
    /// </summary>
    internal static bool IsVirtualMonitorConnected(string selector, IEnumerable<DisplayConfigInterop.MonitorTarget> targets)
    {
        Func<DisplayConfigInterop.MonitorTarget, bool>? matches = selector.ToLowerInvariant() switch
        {
            "auto" => t => IsNexScreenName(t.FriendlyName, t.EdidManufacturer) || IsVddName(t.FriendlyName, t.EdidManufacturer),
            "nexscreen" => t => IsNexScreenName(t.FriendlyName, t.EdidManufacturer),
            "vdd" => t => IsVddName(t.FriendlyName, t.EdidManufacturer),
            _ => null,
        };
        return matches is not null && targets.Any(matches);
    }

    /// <summary>Live check of <see cref="IsVirtualMonitorConnected(string, IEnumerable{DisplayConfigInterop.MonitorTarget})"/>.</summary>
    public static bool IsVirtualMonitorConnected(string selector)
    {
        try
        {
            return IsVirtualMonitorConnected(selector, DisplayConfigInterop.QueryAvailableTargets());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Live check of <see cref="IsVirtualMonitorDetached(string, IEnumerable{DisplayConfigInterop.MonitorTarget})"/>.</summary>
    public static bool IsVirtualMonitorDetached(string selector)
    {
        try
        {
            return IsVirtualMonitorDetached(selector, DisplayConfigInterop.QueryAvailableTargets());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsNexScreenName(string friendlyName, string? manufacturer) =>
        string.Equals(friendlyName, NexScreenMonitorName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(manufacturer, NexScreenManufacturer, StringComparison.Ordinal);

    internal static bool IsVddName(string friendlyName, string? manufacturer) =>
        string.Equals(manufacturer, VddManufacturer, StringComparison.Ordinal) ||
        friendlyName.StartsWith(VddMonitorNamePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the selector: "auto" (default: the NexScreen monitor, else the VDD virtual monitor), "nexscreen"
    /// (by EDID name/manufacturer), "vdd", "primary", or a GDI name like \\.\DISPLAY2.
    /// </summary>
    public static DisplayOutputInfo? Find(string selector, List<DisplayOutputInfo>? outputs = null)
    {
        outputs ??= Enumerate();
        if (string.Equals(selector, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return outputs.FirstOrDefault(o => o.IsNexScreen) ?? outputs.FirstOrDefault(o => o.IsVdd);
        }

        if (string.Equals(selector, "vdd", StringComparison.OrdinalIgnoreCase))
        {
            return outputs.FirstOrDefault(o => o.IsVdd);
        }

        if (string.Equals(selector, "nexscreen", StringComparison.OrdinalIgnoreCase))
        {
            return outputs.FirstOrDefault(o => o.IsNexScreen);
        }

        if (string.Equals(selector, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return outputs.FirstOrDefault(o => o.IsPrimary);
        }

        return outputs.FirstOrDefault(o => string.Equals(o.GdiName, selector, StringComparison.OrdinalIgnoreCase));
    }
}
