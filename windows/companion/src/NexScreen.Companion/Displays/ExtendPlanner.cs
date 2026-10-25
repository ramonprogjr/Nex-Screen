using static NexScreen.Companion.Displays.DisplayConfigInterop;

namespace NexScreen.Companion.Displays;

internal enum ExtendPlanKind { Ready, AlreadyExtended, NoVirtualTarget, NoFreeSource }

internal sealed record PreferredMode(uint Width, uint Height, VideoSignalInfo TargetMode);

/// <summary>Desktop rectangle of the virtual monitor while it was extended (to put it back in the same place).</summary>
public readonly record struct DesktopRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public override string ToString() => $"{X},{Y},{Width},{Height}";

    public static DesktopRect? Parse(string? text)
    {
        var parts = text?.Split(',');
        if (parts is not { Length: 4 })
        {
            return null;
        }

        var v = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out v[i]))
            {
                return null;
            }
        }

        return v[2] > 0 && v[3] > 0 ? new DesktopRect(v[0], v[1], v[2], v[3]) : null;
    }

    internal static DesktopRect Of(SourceMode m) => new(m.X, m.Y, (int)m.Width, (int)m.Height);
}

/// <param name="Paths">Paths to pass to SetDisplayConfig (all active).</param>
/// <param name="Modes">Modes referenced by <paramref name="Paths"/>, re-indexed.</param>
internal sealed record ExtendPlan(ExtendPlanKind Kind, PathInfo[] Paths, ModeInfo[] Modes, string Summary)
{
    public static ExtendPlan Fail(ExtendPlanKind kind, string summary) => new(kind, [], [], summary);
}

/// <summary>
/// Pure part of the targeted extend: from the active configuration (QDC_ONLY_ACTIVE_PATHS) and all possible paths
/// (QDC_ALL_PATHS), builds a configuration that keeps every active path as is, drops a virtual-monitor path that
/// mirrors another display, and activates the virtual monitor on a free source to the right of the desktop.
/// </summary>
internal static class ExtendPlanner
{
    internal static bool SameSource(in PathInfo a, in PathInfo b) =>
        a.SourceInfo.AdapterId.Value == b.SourceInfo.AdapterId.Value && a.SourceInfo.Id == b.SourceInfo.Id;

    internal static bool SameTarget(in PathInfo a, in PathInfo b) =>
        a.TargetInfo.AdapterId.Value == b.TargetInfo.AdapterId.Value && a.TargetInfo.Id == b.TargetInfo.Id;

    /// <summary>"Duplicate": the path shares its source (same adapter and source id) with another active path.</summary>
    internal static bool IsCloned(IReadOnlyList<PathInfo> active, int index)
    {
        for (var i = 0; i < active.Count; i++)
        {
            if (i != index && SameSource(active[i], active[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Right of the right-most desktop rectangle, top-aligned with it; (0,0) for an empty desktop.</summary>
    internal static (int X, int Y) PositionRightOf(IEnumerable<SourceMode> sources)
    {
        (int Right, int Top)? best = null;
        foreach (var m in sources)
        {
            var right = m.X + (int)m.Width;
            if (best is null || right > best.Value.Right)
            {
                best = (right, m.Y);
            }
        }

        return best is { } b ? (b.Right, b.Top) : (0, 0);
    }

    /// <summary>
    /// Where to put a <paramref name="width"/> x <paramref name="height"/> virtual monitor: where it was before
    /// (<paramref name="remembered"/>) when that spot is still free and next to a display, otherwise right of the desktop.
    /// </summary>
    internal static (int X, int Y) ChoosePosition(IReadOnlyCollection<SourceMode> kept, uint width, uint height, DesktopRect? remembered)
    {
        if (remembered is { } r && r.Width == width && r.Height == height && kept.Count > 0)
        {
            var rects = kept.Select(DesktopRect.Of).ToList();
            var overlaps = rects.Any(k => r.X < k.Right && k.X < r.Right && r.Y < k.Bottom && k.Y < r.Bottom);
            var touches = rects.Any(k =>
                ((r.Right == k.X || k.Right == r.X) && r.Y < k.Bottom && k.Y < r.Bottom) ||
                ((r.Bottom == k.Y || k.Bottom == r.Y) && r.X < k.Right && k.X < r.Right));
            if (!overlaps && touches)
            {
                return (r.X, r.Y);
            }
        }

        return PositionRightOf(kept);
    }

    /// <summary>Desktop rectangle of the virtual monitor when it is extended (active, own source); null otherwise.</summary>
    internal static DesktopRect? FindExtendedVirtual(PathInfo[] active, ModeInfo[] modes, Func<PathInfo, int> virtualRank)
    {
        for (var i = 0; i < active.Length; i++)
        {
            var idx = active[i].SourceInfo.ModeInfoIdx;
            if (virtualRank(active[i]) >= 0 && !IsCloned(active, i) && idx < modes.Length && modes[idx].InfoType == ModeTypeSource)
            {
                return DesktopRect.Of(modes[idx].SourceMode);
            }
        }

        return null;
    }

    /// <param name="virtualRank">-1 for a target that is not the virtual monitor; lower is preferred.</param>
    /// <param name="preferredMode">Mode for the activated target; null (or <paramref name="supplyModes"/> false) lets Windows pick.</param>
    internal static ExtendPlan Build(PathInfo[] active, ModeInfo[] activeModes, PathInfo[] all, Func<PathInfo, int> virtualRank,
        Func<PathInfo, PreferredMode?> preferredMode, bool supplyModes = true, DesktopRect? remembered = null)
    {
        var dropped = new List<int>();
        for (var i = 0; i < active.Length; i++)
        {
            if (virtualRank(active[i]) < 0)
            {
                continue;
            }

            if (!IsCloned(active, i))
            {
                return ExtendPlan.Fail(ExtendPlanKind.AlreadyExtended, "the virtual monitor already has its own desktop area");
            }

            dropped.Add(i);
        }

        var kept = active.Where((_, i) => !dropped.Contains(i)).ToList();

        // Highest-priority virtual target that can be shown.
        var candidates = all
            .Select((p, order) => (Path: p, Order: order, Rank: virtualRank(p)))
            .Where(c => c.Rank >= 0 && c.Path.TargetInfo.TargetAvailable != 0)
            .OrderBy(c => c.Rank).ThenBy(c => c.Order)
            .ToList();
        if (candidates.Count == 0)
        {
            return ExtendPlan.Fail(ExtendPlanKind.NoVirtualTarget, "no virtual monitor target is available");
        }

        var target = candidates[0].Path;
        PathInfo? chosen = null;
        foreach (var c in candidates.Where(c => SameTarget(c.Path, target)))
        {
            if (!kept.Any(k => SameSource(k, c.Path)))
            {
                chosen = c.Path;
                break;
            }
        }

        if (chosen is null)
        {
            // A target in use (e.g. duplicated) is only listed with its current source: move it to another source
            // of the same adapter that no kept path uses.
            var adapter = target.SourceInfo.AdapterId.Value;
            var free = all.Where(p => p.SourceInfo.AdapterId.Value == adapter)
                .Select(p => p.SourceInfo.Id)
                .Distinct()
                .Order()
                .Where(id => !kept.Any(k => k.SourceInfo.AdapterId.Value == adapter && k.SourceInfo.Id == id))
                .Select(id => (uint?)id)
                .FirstOrDefault();
            if (free is { } id)
            {
                var moved = target;
                moved.SourceInfo.Id = id;
                moved.SourceInfo.StatusFlags = 0;
                chosen = moved;
            }
        }

        if (chosen is not { } path)
        {
            return ExtendPlan.Fail(ExtendPlanKind.NoFreeSource, "no free display source for the virtual monitor");
        }

        // Re-index the modes the kept paths use; the dropped clone's modes go away with it.
        var modes = new List<ModeInfo>();
        var remap = new Dictionary<uint, uint>();
        uint Remap(uint idx)
        {
            if (idx == ModeIdxInvalid || idx >= activeModes.Length)
            {
                return ModeIdxInvalid;
            }

            if (!remap.TryGetValue(idx, out var n))
            {
                n = (uint)modes.Count;
                modes.Add(activeModes[idx]);
                remap[idx] = n;
            }

            return n;
        }

        var paths = new List<PathInfo>();
        foreach (var k in kept)
        {
            var p = k;
            p.SourceInfo.ModeInfoIdx = Remap(k.SourceInfo.ModeInfoIdx);
            p.TargetInfo.ModeInfoIdx = Remap(k.TargetInfo.ModeInfoIdx);
            paths.Add(p);
        }

        path.Flags = PathActive;
        if (path.TargetInfo.Rotation == 0)
        {
            path.TargetInfo.Rotation = RotationIdentity;
        }

        if (path.TargetInfo.Scaling == 0)
        {
            path.TargetInfo.Scaling = ScalingPreferred;
        }

        var preferred = supplyModes ? preferredMode(path) : null;
        string summary;
        if (preferred is not null)
        {
            var keptSources = modes.Where(m => m.InfoType == ModeTypeSource).Select(m => m.SourceMode).ToList();
            var (x, y) = ChoosePosition(keptSources, preferred.Width, preferred.Height, remembered);
            path.SourceInfo.ModeInfoIdx = (uint)modes.Count;
            modes.Add(new ModeInfo
            {
                InfoType = ModeTypeSource,
                Id = path.SourceInfo.Id,
                AdapterId = path.SourceInfo.AdapterId,
                SourceMode = new SourceMode { Width = preferred.Width, Height = preferred.Height, PixelFormat = PixelFormat32Bpp, X = x, Y = y },
            });
            path.TargetInfo.ModeInfoIdx = (uint)modes.Count;
            modes.Add(new ModeInfo
            {
                InfoType = ModeTypeTarget,
                Id = path.TargetInfo.Id,
                AdapterId = path.TargetInfo.AdapterId,
                TargetMode = preferred.TargetMode,
            });
            if (path.TargetInfo.RefreshRate.Denominator == 0)
            {
                path.TargetInfo.RefreshRate = preferred.TargetMode.VSyncFreq;
            }

            summary = $"{preferred.Width}x{preferred.Height} at ({x},{y})";
        }
        else
        {
            path.SourceInfo.ModeInfoIdx = ModeIdxInvalid;
            path.TargetInfo.ModeInfoIdx = ModeIdxInvalid;
            summary = "mode and position chosen by Windows";
        }

        paths.Add(path);
        summary = $"{(dropped.Count > 0 ? "replaced the duplicated virtual monitor path; " : string.Empty)}" +
                  $"kept {kept.Count} active path(s); virtual monitor on source {path.SourceInfo.Id}, {summary}";
        return new ExtendPlan(ExtendPlanKind.Ready, [.. paths], [.. modes], summary);
    }
}
