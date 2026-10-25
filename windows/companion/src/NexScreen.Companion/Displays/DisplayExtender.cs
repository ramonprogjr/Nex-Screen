using System.Diagnostics;
using NexScreen.Companion.Util;
using static NexScreen.Companion.Displays.DisplayConfigInterop;

namespace NexScreen.Companion.Displays;

public enum ExtendMethod { SetDisplayConfig, AlreadyExtended, DisplaySwitch }

/// <summary>
/// Brings the virtual monitor back into the desktop without touching the other displays: SetDisplayConfig with the
/// current configuration plus the virtual monitor on its own source (see <see cref="ExtendPlanner"/>). Falls back to
/// DisplaySwitch.exe /extend (Win+P > Extend, which re-extends every display) when that is rejected.
/// </summary>
public static class DisplayExtender
{
    private const uint ValidateFlags = SdcValidate | SdcUseSuppliedDisplayConfig | SdcAllowChanges;
    private const uint ApplyFlags = SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase;

    /// <param name="remembered">Where the virtual monitor was while extended; reused when that spot is still free.</param>
    public static ExtendMethod Extend(string selector, DesktopRect? remembered = null)
    {
        if (!DisplayEnumerator.IsVirtualSelector(selector))
        {
            Log.Info($"Output selector \"{selector}\" is not a virtual monitor; running DisplaySwitch.exe /extend.");
            RunDisplaySwitch();
            return ExtendMethod.DisplaySwitch;
        }

        try
        {
            var (active, activeModes) = QueryConfig(QdcOnlyActivePaths);
            var (all, _) = QueryConfig(QdcAllPaths);
            var rank = RankFunction(selector);

            for (var i = 0; i < active.Length; i++)
            {
                var p = active[i];
                Log.Debug($"Extend: active path {i}: source {p.SourceInfo.AdapterId.Value:X}:{p.SourceInfo.Id} -> target " +
                          $"{p.TargetInfo.AdapterId.Value:X}:{p.TargetInfo.Id}, virtual={rank(p) >= 0}, cloned={ExtendPlanner.IsCloned(active, i)}");
            }

            if (Log.MinLevel <= LogLevel.Debug)
            {
                var sources = all.Where(p => rank(p) >= 0)
                    .Select(p => $"{p.SourceInfo.AdapterId.Value:X}:{p.SourceInfo.Id}{(p.IsActive ? "*" : string.Empty)}{(p.TargetInfo.TargetAvailable == 0 ? "(unavailable)" : string.Empty)}");
                Log.Debug($"Extend: {all.Length} possible paths; virtual monitor sources: {string.Join(" ", sources)}");
            }

            static PreferredMode? Preferred(PathInfo p) =>
                GetPreferredMode(p.TargetInfo.AdapterId, p.TargetInfo.Id) is { } m ? new PreferredMode(m.Width, m.Height, m.TargetMode) : null;

            // First with our mode and position; then let Windows pick them for the new path.
            foreach (var supplyModes in new[] { true, false })
            {
                var plan = ExtendPlanner.Build(active, activeModes, all, rank, Preferred, supplyModes, remembered);
                if (plan.Kind == ExtendPlanKind.AlreadyExtended)
                {
                    Log.Info($"Extend: nothing to do, {plan.Summary}.");
                    return ExtendMethod.AlreadyExtended;
                }

                if (plan.Kind != ExtendPlanKind.Ready)
                {
                    Log.Warn($"Extend: SetDisplayConfig not possible ({plan.Summary}).");
                    break;
                }

                var rc = Apply(plan.Paths, plan.Modes, ValidateFlags);
                if (rc == 0)
                {
                    rc = Apply(plan.Paths, plan.Modes, ApplyFlags);
                    if (rc == 0)
                    {
                        Log.Info($"Extend: SetDisplayConfig attached only the virtual monitor ({plan.Summary}).");
                        return ExtendMethod.SetDisplayConfig;
                    }

                    Log.Warn($"Extend: SetDisplayConfig apply failed (error {rc}; {plan.Summary}).");
                }
                else
                {
                    Log.Info($"Extend: SetDisplayConfig validation failed (error {rc}; {plan.Summary}).");
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"Extend: display configuration query failed: {ex.Message}");
        }

        Log.Info("Extend: falling back to DisplaySwitch.exe /extend.");
        RunDisplaySwitch();
        return ExtendMethod.DisplaySwitch;
    }

    /// <summary>Desktop rectangle of the selector's virtual monitor while it is extended; null otherwise.</summary>
    public static DesktopRect? CurrentVirtualRect(string selector)
    {
        if (!DisplayEnumerator.IsVirtualSelector(selector))
        {
            return null;
        }

        try
        {
            var (active, modes) = QueryConfig(QdcOnlyActivePaths);
            return ExtendPlanner.FindExtendedVirtual(active, modes, RankFunction(selector));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Func<PathInfo, int> RankFunction(string selector)
    {
        var ranks = new Dictionary<(long, uint), int>();
        return p =>
        {
            var key = (p.TargetInfo.AdapterId.Value, p.TargetInfo.Id);
            if (!ranks.TryGetValue(key, out var rank))
            {
                var (friendly, manufacturer) = GetTargetName(p.TargetInfo.AdapterId, p.TargetInfo.Id);
                rank = DisplayEnumerator.VirtualRank(selector, friendly, manufacturer);
                ranks[key] = rank;
            }

            return rank;
        };
    }

    /// <summary>Same as Win+P > Extend.</summary>
    public static void RunDisplaySwitch()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "/extend") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Could not run DisplaySwitch.exe: {ex.Message}");
        }
    }
}
