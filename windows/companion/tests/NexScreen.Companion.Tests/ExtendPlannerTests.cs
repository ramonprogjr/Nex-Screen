using System.Runtime.InteropServices;
using NexScreen.Companion.Displays;
using static NexScreen.Companion.Displays.DisplayConfigInterop;

namespace NexScreen.Companion.Tests;

/// <summary>Targeted extend on synthetic QueryDisplayConfig arrays (no real display is touched).</summary>
public class ExtendPlannerTests
{
    private const long Gpu = 0x1_0000_0010;  // adapter of the internal panel (and an external monitor)
    private const long Vdd = 0x2_0000_0020;  // IddCx adapter of the virtual monitor
    private const uint Panel = 1;
    private const uint External = 3;
    private const uint VirtualTarget = 5;
    private const uint NoMode = ModeIdxInvalid;

    private static PathInfo P(long srcAdapter, uint src, long tgtAdapter, uint tgt, bool active, uint srcMode = NoMode, uint tgtMode = NoMode)
    {
        var p = new PathInfo { Flags = active ? PathActive : 0 };
        p.SourceInfo.AdapterId = Luid.From(srcAdapter);
        p.SourceInfo.Id = src;
        p.SourceInfo.ModeInfoIdx = srcMode;
        p.TargetInfo.AdapterId = Luid.From(tgtAdapter);
        p.TargetInfo.Id = tgt;
        p.TargetInfo.ModeInfoIdx = tgtMode;
        p.TargetInfo.TargetAvailable = 1;
        p.TargetInfo.Rotation = RotationIdentity;
        p.TargetInfo.Scaling = 1;
        p.TargetInfo.RefreshRate = new Rational { Numerator = 60, Denominator = 1 };
        return p;
    }

    private static ModeInfo Src(long adapter, uint id, uint w, uint h, int x, int y) => new()
    {
        InfoType = ModeTypeSource,
        Id = id,
        AdapterId = Luid.From(adapter),
        SourceMode = new SourceMode { Width = w, Height = h, PixelFormat = PixelFormat32Bpp, X = x, Y = y },
    };

    private static ModeInfo Tgt(long adapter, uint id, uint w, uint h) => new()
    {
        InfoType = ModeTypeTarget,
        Id = id,
        AdapterId = Luid.From(adapter),
        TargetMode = new VideoSignalInfo
        {
            ActiveSize = new Region2D { Cx = w, Cy = h },
            TotalSize = new Region2D { Cx = w, Cy = h },
            VSyncFreq = new Rational { Numerator = 60, Denominator = 1 },
        },
    };

    private static int Rank(PathInfo p) => p.TargetInfo.AdapterId.Value == Vdd && p.TargetInfo.Id == VirtualTarget ? 0 : -1;

    private static readonly PreferredMode Preferred = new(1728, 1080, Tgt(Vdd, VirtualTarget, 1728, 1080).TargetMode);

    private static PreferredMode? PreferredOf(PathInfo _) => Preferred;

    // All possible paths of the virtual target: one per source of each adapter, as QDC_ALL_PATHS lists them.
    private static IEnumerable<PathInfo> VirtualCandidates(bool includeVddAdapter = false)
    {
        for (uint s = 0; s < 4; s++)
        {
            yield return P(Gpu, s, Vdd, VirtualTarget, false);
        }

        if (includeVddAdapter)
        {
            yield return P(Vdd, 0, Vdd, VirtualTarget, false);
        }
    }

    [Fact]
    public void Interop_StructSizesMatchWin32()
    {
        Assert.Equal(72, Marshal.SizeOf<PathInfo>());
        Assert.Equal(64, Marshal.SizeOf<ModeInfo>());
    }

    [Fact]
    public void IsCloned_SameAdapterAndSourceId()
    {
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true), P(Gpu, 0, Vdd, VirtualTarget, true), P(Gpu, 1, Gpu, External, true)];
        Assert.True(ExtendPlanner.IsCloned(active, 0));
        Assert.True(ExtendPlanner.IsCloned(active, 1));
        Assert.False(ExtendPlanner.IsCloned(active, 2));

        // Same source id on another adapter is a different source.
        PathInfo[] separate = [P(Gpu, 0, Gpu, Panel, true), P(Vdd, 0, Vdd, VirtualTarget, true)];
        Assert.False(ExtendPlanner.IsCloned(separate, 0));
        Assert.False(ExtendPlanner.IsCloned(separate, 1));
    }

    [Fact]
    public void Position_RightOfRightMostTopAligned()
    {
        Assert.Equal((3840, 0), ExtendPlanner.PositionRightOf([Src(Gpu, 0, 3840, 2400, 0, 0).SourceMode]));
        Assert.Equal((1920, 0), ExtendPlanner.PositionRightOf(
            [Src(Gpu, 0, 1920, 1080, 0, 0).SourceMode, Src(Gpu, 1, 1280, 1024, -1280, 100).SourceMode]));
        Assert.Equal((3200, -200), ExtendPlanner.PositionRightOf(
            [Src(Gpu, 0, 1920, 1080, 0, 0).SourceMode, Src(Gpu, 1, 1280, 1024, 1920, -200).SourceMode]));
        Assert.Equal((0, 0), ExtendPlanner.PositionRightOf([]));
    }

    [Fact]
    public void Duplicate_ReplacesClonedVirtualPathAndKeepsPanel()
    {
        ModeInfo[] modes = [Src(Gpu, 0, 3840, 2400, 0, 0), Tgt(Gpu, Panel, 3840, 2400), Tgt(Vdd, VirtualTarget, 3840, 2400)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1), P(Gpu, 0, Vdd, VirtualTarget, true, 0, 2)];
        PathInfo[] all = [.. active, .. VirtualCandidates(), P(Gpu, 1, Gpu, Panel, false)];

        var plan = ExtendPlanner.Build(active, modes, all, Rank, PreferredOf);

        Assert.Equal(ExtendPlanKind.Ready, plan.Kind);
        Assert.Equal(2, plan.Paths.Length);
        Assert.Equal(active[0], plan.Paths[0]); // panel untouched (its modes keep indices 0 and 1)
        var v = plan.Paths[1];
        Assert.True(v.IsActive);
        Assert.Equal((Gpu, 1u), (v.SourceInfo.AdapterId.Value, v.SourceInfo.Id)); // own, free source
        Assert.Equal((Vdd, VirtualTarget), (v.TargetInfo.AdapterId.Value, v.TargetInfo.Id));
        Assert.False(ExtendPlanner.IsCloned(plan.Paths, 1));

        Assert.Equal(4, plan.Modes.Length); // the clone's 3840x2400 target mode is gone
        Assert.Equal(modes[0], plan.Modes[0]);
        Assert.Equal(modes[1], plan.Modes[1]);
        var src = plan.Modes[v.SourceInfo.ModeInfoIdx];
        Assert.Equal(ModeTypeSource, src.InfoType);
        Assert.Equal(1u, src.Id);
        Assert.Equal((1728u, 1080u, 3840, 0), (src.SourceMode.Width, src.SourceMode.Height, src.SourceMode.X, src.SourceMode.Y));
        var tgt = plan.Modes[v.TargetInfo.ModeInfoIdx];
        Assert.Equal((ModeTypeTarget, VirtualTarget, 1728u), (tgt.InfoType, tgt.Id, tgt.TargetMode.ActiveSize.Cx));
    }

    [Fact]
    public void Duplicate_TargetListedOnlyWithItsCloneSource_MovesToFreeSource()
    {
        // What QDC_ALL_PATHS returns on a real Iris Xe + VDD laptop in "Duplicate": the virtual target appears only
        // on its current (shared) source; other paths reveal the adapter's other source ids.
        ModeInfo[] modes = [Src(Gpu, 0, 3840, 2400, 0, 0), Tgt(Gpu, Panel, 3840, 2400), Tgt(Vdd, VirtualTarget, 1728, 1080)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1), P(Gpu, 0, Vdd, VirtualTarget, true, 0, 2)];
        PathInfo[] all = [.. active, P(Gpu, 2, Gpu, External, false), P(Gpu, 1, Gpu, External, false), P(Gpu, 1, Gpu, Panel, false)];

        var plan = ExtendPlanner.Build(active, modes, all, Rank, PreferredOf);

        Assert.Equal(ExtendPlanKind.Ready, plan.Kind);
        Assert.Equal(active[0], plan.Paths[0]);
        var v = plan.Paths[1];
        Assert.Equal((Gpu, 1u), (v.SourceInfo.AdapterId.Value, v.SourceInfo.Id));
        Assert.Equal((Vdd, VirtualTarget), (v.TargetInfo.AdapterId.Value, v.TargetInfo.Id));
        Assert.Equal(1u, plan.Modes[v.SourceInfo.ModeInfoIdx].Id);
        Assert.Equal((3840, 0), (plan.Modes[v.SourceInfo.ModeInfoIdx].SourceMode.X, plan.Modes[v.SourceInfo.ModeInfoIdx].SourceMode.Y));
    }

    [Fact]
    public void SingleScreen_ActivatesVirtualOnFreeSourceRightOfPanel()
    {
        ModeInfo[] modes = [Src(Gpu, 0, 3840, 2400, 0, 0), Tgt(Gpu, Panel, 3840, 2400)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1)];
        PathInfo[] all = [.. active, .. VirtualCandidates()];

        var plan = ExtendPlanner.Build(active, modes, all, Rank, PreferredOf);

        Assert.Equal(ExtendPlanKind.Ready, plan.Kind);
        Assert.Equal(active[0], plan.Paths[0]);
        Assert.Equal(1u, plan.Paths[1].SourceInfo.Id);
        var src = plan.Modes[plan.Paths[1].SourceInfo.ModeInfoIdx].SourceMode;
        Assert.Equal((3840, 0), (src.X, src.Y));
    }

    [Fact]
    public void SingleScreen_VirtualOnItsOwnAdapter()
    {
        ModeInfo[] modes = [Src(Gpu, 0, 1920, 1080, 0, 0), Tgt(Gpu, Panel, 1920, 1080)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1)];
        PathInfo[] all = [.. active, P(Vdd, 0, Vdd, VirtualTarget, false)];

        var plan = ExtendPlanner.Build(active, modes, all, Rank, PreferredOf);

        Assert.Equal(ExtendPlanKind.Ready, plan.Kind);
        Assert.Equal((Vdd, 0u), (plan.Paths[1].SourceInfo.AdapterId.Value, plan.Paths[1].SourceInfo.Id));
        Assert.Equal(1920, plan.Modes[plan.Paths[1].SourceInfo.ModeInfoIdx].SourceMode.X);
    }

    [Fact]
    public void OtherDisplays_KeepTheirPathsAndModes()
    {
        // Panel at (0,0), external monitor at the left (-1920,0), virtual monitor duplicating the external one.
        // The clone's target mode sits in the middle of the array, so the kept paths' indices must shift.
        ModeInfo[] modes =
        [
            Src(Gpu, 0, 2560, 1600, 0, 0), Tgt(Vdd, VirtualTarget, 1920, 1080), Tgt(Gpu, Panel, 2560, 1600),
            Src(Gpu, 1, 1920, 1080, -1920, 0), Tgt(Gpu, External, 1920, 1080),
        ];
        PathInfo[] active =
        [
            P(Gpu, 0, Gpu, Panel, true, 0, 2),
            P(Gpu, 1, Vdd, VirtualTarget, true, 3, 1),
            P(Gpu, 1, Gpu, External, true, 3, 4),
        ];
        PathInfo[] all = [.. active, .. VirtualCandidates()];

        var plan = ExtendPlanner.Build(active, modes, all, Rank, PreferredOf);

        Assert.Equal(ExtendPlanKind.Ready, plan.Kind);
        Assert.Equal(3, plan.Paths.Length);
        foreach (var (kept, original) in new[] { (plan.Paths[0], active[0]), (plan.Paths[1], active[2]) })
        {
            // Same path, and the same mode contents behind the (re-indexed) mode indices.
            Assert.Equal(original, WithModeIndices(kept, original.SourceInfo.ModeInfoIdx, original.TargetInfo.ModeInfoIdx));
            Assert.Equal(modes[original.SourceInfo.ModeInfoIdx], plan.Modes[kept.SourceInfo.ModeInfoIdx]);
            Assert.Equal(modes[original.TargetInfo.ModeInfoIdx], plan.Modes[kept.TargetInfo.ModeInfoIdx]);
        }

        var v = plan.Paths[2];
        Assert.Equal(2u, v.SourceInfo.Id); // sources 0 and 1 are taken
        var src = plan.Modes[v.SourceInfo.ModeInfoIdx].SourceMode;
        Assert.Equal((2560, 0), (src.X, src.Y));
        Assert.DoesNotContain(plan.Modes, m => m.InfoType == ModeTypeTarget && m.Id == VirtualTarget && m.TargetMode.ActiveSize.Cx == 1920);
        Assert.Equal(6, plan.Modes.Length);
        Assert.Equal(1u, plan.Paths[0].TargetInfo.ModeInfoIdx); // was 2 before the clone's mode was dropped
    }

    private static PathInfo WithModeIndices(PathInfo p, uint source, uint target)
    {
        p.SourceInfo.ModeInfoIdx = source;
        p.TargetInfo.ModeInfoIdx = target;
        return p;
    }

    [Fact]
    public void AlreadyExtended_NothingToDo()
    {
        ModeInfo[] modes = [Src(Gpu, 0, 3840, 2400, 0, 0), Tgt(Gpu, Panel, 3840, 2400), Src(Gpu, 1, 1728, 1080, 3840, 0), Tgt(Vdd, VirtualTarget, 1728, 1080)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1), P(Gpu, 1, Vdd, VirtualTarget, true, 2, 3)];
        var plan = ExtendPlanner.Build(active, modes, [.. active, .. VirtualCandidates()], Rank, PreferredOf);
        Assert.Equal(ExtendPlanKind.AlreadyExtended, plan.Kind);
    }

    [Fact]
    public void NoTargetOrNoSource_Fails()
    {
        ModeInfo[] modes = [Src(Gpu, 0, 3840, 2400, 0, 0), Tgt(Gpu, Panel, 3840, 2400)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1)];

        Assert.Equal(ExtendPlanKind.NoVirtualTarget, ExtendPlanner.Build(active, modes, active, Rank, PreferredOf).Kind);

        var unavailable = P(Gpu, 1, Vdd, VirtualTarget, false);
        unavailable.TargetInfo.TargetAvailable = 0;
        Assert.Equal(ExtendPlanKind.NoVirtualTarget, ExtendPlanner.Build(active, modes, [.. active, unavailable], Rank, PreferredOf).Kind);

        // The only source the virtual target can use is the panel's.
        Assert.Equal(ExtendPlanKind.NoFreeSource,
            ExtendPlanner.Build(active, modes, [.. active, P(Gpu, 0, Vdd, VirtualTarget, false)], Rank, PreferredOf).Kind);
    }

    [Fact]
    public void WithoutModes_WindowsPicksModeAndPosition()
    {
        ModeInfo[] modes = [Src(Gpu, 0, 3840, 2400, 0, 0), Tgt(Gpu, Panel, 3840, 2400)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1)];
        PathInfo[] all = [.. active, .. VirtualCandidates()];

        var noPreferred = ExtendPlanner.Build(active, modes, all, Rank, _ => null);
        var notSupplied = ExtendPlanner.Build(active, modes, all, Rank, PreferredOf, supplyModes: false);

        foreach (var plan in new[] { noPreferred, notSupplied })
        {
            Assert.Equal(ExtendPlanKind.Ready, plan.Kind);
            Assert.Equal(2, plan.Modes.Length);
            Assert.Equal(ModeIdxInvalid, plan.Paths[1].SourceInfo.ModeInfoIdx);
            Assert.Equal(ModeIdxInvalid, plan.Paths[1].TargetInfo.ModeInfoIdx);
            Assert.True(plan.Paths[1].IsActive);
        }
    }

    [Fact]
    public void PrefersLowerRankTarget()
    {
        const uint nexScreenTarget = 7;
        int TwoVirtuals(PathInfo p) => p.TargetInfo.Id == nexScreenTarget ? 0 : Rank(p) >= 0 ? 1 : -1;
        ModeInfo[] modes = [Src(Gpu, 0, 1920, 1080, 0, 0), Tgt(Gpu, Panel, 1920, 1080)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1)];
        PathInfo[] all = [.. active, .. VirtualCandidates(), P(Gpu, 2, Vdd, nexScreenTarget, false)];

        var plan = ExtendPlanner.Build(active, modes, all, TwoVirtuals, PreferredOf);

        Assert.Equal(nexScreenTarget, plan.Paths[1].TargetInfo.Id);
    }

    [Fact]
    public void RememberedPlace_ReusedWhenFreeAndAdjacent()
    {
        SourceMode[] panel = [Src(Gpu, 0, 3840, 2400, 0, 0).SourceMode];
        var left = new DesktopRect(-1728, 174, 1728, 1080);
        Assert.Equal((-1728, 174), ExtendPlanner.ChoosePosition(panel, 1728, 1080, left));
        Assert.Equal((3840, 0), ExtendPlanner.ChoosePosition(panel, 1728, 1080, null));
        Assert.Equal((3840, 0), ExtendPlanner.ChoosePosition(panel, 1920, 1080, left)); // other size
        Assert.Equal((3840, 0), ExtendPlanner.ChoosePosition(panel, 1728, 1080, new DesktopRect(0, 0, 1728, 1080))); // overlaps
        Assert.Equal((3840, 0), ExtendPlanner.ChoosePosition(panel, 1728, 1080, new DesktopRect(-5000, 0, 1728, 1080))); // detached island
        Assert.Equal((100, 2400), ExtendPlanner.ChoosePosition(panel, 1728, 1080, new DesktopRect(100, 2400, 1728, 1080))); // below
        Assert.Equal((3840, 0), ExtendPlanner.ChoosePosition(panel, 1728, 1080, new DesktopRect(-1728, 2400, 1728, 1080))); // corner only
    }

    [Fact]
    public void Build_UsesRememberedPlace()
    {
        ModeInfo[] modes = [Src(Gpu, 0, 3840, 2400, 0, 0), Tgt(Gpu, Panel, 3840, 2400)];
        PathInfo[] active = [P(Gpu, 0, Gpu, Panel, true, 0, 1)];
        var plan = ExtendPlanner.Build(active, modes, [.. active, .. VirtualCandidates()], Rank, PreferredOf,
            remembered: new DesktopRect(-1728, 174, 1728, 1080));
        var src = plan.Modes[plan.Paths[1].SourceInfo.ModeInfoIdx].SourceMode;
        Assert.Equal((-1728, 174), (src.X, src.Y));
    }

    [Fact]
    public void FindExtendedVirtual_OnlyWhenOwnSource()
    {
        ModeInfo[] modes = [Src(Gpu, 0, 3840, 2400, 0, 0), Tgt(Gpu, Panel, 3840, 2400), Src(Gpu, 1, 1728, 1080, -1728, 174), Tgt(Vdd, VirtualTarget, 1728, 1080)];
        PathInfo[] extended = [P(Gpu, 0, Gpu, Panel, true, 0, 1), P(Gpu, 1, Vdd, VirtualTarget, true, 2, 3)];
        Assert.Equal(new DesktopRect(-1728, 174, 1728, 1080), ExtendPlanner.FindExtendedVirtual(extended, modes, Rank));

        PathInfo[] cloned = [P(Gpu, 0, Gpu, Panel, true, 0, 1), P(Gpu, 0, Vdd, VirtualTarget, true, 0, 3)];
        Assert.Null(ExtendPlanner.FindExtendedVirtual(cloned, modes, Rank));
        Assert.Null(ExtendPlanner.FindExtendedVirtual([extended[0]], modes, Rank));
    }

    [Theory]
    [InlineData("-1728,174,1728,1080", true)]
    [InlineData("3840,0,1920,1200", true)]
    [InlineData("1,2,3", false)]
    [InlineData("a,0,1728,1080", false)]
    [InlineData("0,0,0,1080", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DesktopRect_RoundTrip(string? text, bool valid)
    {
        var r = DesktopRect.Parse(text);
        Assert.Equal(valid, r is not null);
        if (r is { } rect)
        {
            Assert.Equal(text, rect.ToString());
        }
    }

    [Theory]
    [InlineData("auto", "NexScreen", "NXS", 0)]
    [InlineData("auto", "VDD by MTT", "MTT", 1)]
    [InlineData("auto", "", "SHP", -1)]
    [InlineData("vdd", "VDD by MTT", null, 0)]
    [InlineData("VDD", "NexScreen", "NXS", -1)]
    [InlineData("nexscreen", "NexScreen", null, 0)]
    [InlineData("primary", "VDD by MTT", "MTT", -1)]
    [InlineData(@"\\.\DISPLAY2", "VDD by MTT", "MTT", -1)]
    public void VirtualRank(string selector, string name, string? manufacturer, int expected) =>
        Assert.Equal(expected, DisplayEnumerator.VirtualRank(selector, name, manufacturer));
}
