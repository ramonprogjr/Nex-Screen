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
    public bool IsNexScreen =>
        string.Equals(FriendlyName, DisplayEnumerator.NexScreenMonitorName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(EdidManufacturer, DisplayEnumerator.NexScreenManufacturer, StringComparison.Ordinal);
}

public static class DisplayEnumerator
{
    public const string NexScreenMonitorName = "NexScreen";
    public const string NexScreenManufacturer = "NXS";

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
    /// Resolves the selector: "nexscreen" (default, by EDID name/manufacturer), "primary", or a GDI name like \\.\DISPLAY2.
    /// </summary>
    public static DisplayOutputInfo? Find(string selector, List<DisplayOutputInfo>? outputs = null)
    {
        outputs ??= Enumerate();
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
