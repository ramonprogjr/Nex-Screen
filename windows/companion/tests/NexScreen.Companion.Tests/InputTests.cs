using NexScreen.Companion.Input;
using NexScreen.Companion.Net;

namespace NexScreen.Companion.Tests;

public class InputMappingTests
{
    // Primary 1920x1080 at (0,0) and the tablet's 1728x1080 monitor to its left and 200 px higher.
    private static readonly DesktopRect Primary = new(0, 0, 1920, 1080);
    private static readonly DesktopRect Tablet = new(-1728, -200, 1728, 1080);
    private static readonly DesktopRect VirtualScreen = new(-1728, -200, 3648, 1280);

    [Theory]
    [InlineData(0, 0, -1728, -200)]
    [InlineData(65535, 65535, -1, 879)]
    [InlineData(32768, 32768, -864, 340)]
    public void Normalized_MapsToOutputPixels(int nx, int ny, int x, int y)
    {
        Assert.Equal((x, y), InputMapping.NormalizedToDesktop((ushort)nx, (ushort)ny, Tablet));
    }

    [Fact]
    public void Normalized_CornersStayInsideTheOutput()
    {
        var secondary = new DesktopRect(1920, 0, 1920, 1200);
        Assert.Equal((1920, 0), InputMapping.NormalizedToDesktop(0, 0, secondary));
        Assert.Equal((3839, 1199), InputMapping.NormalizedToDesktop(65535, 65535, secondary));
        for (var n = 0; n <= 65535; n += 97)
        {
            var (x, y) = InputMapping.NormalizedToDesktop((ushort)n, (ushort)n, secondary);
            Assert.True(secondary.Contains(x, y), $"{n} -> ({x},{y})");
        }
    }

    [Fact]
    public void Absolute_VirtualScreenCornersAndNegativeOrigin()
    {
        Assert.Equal((0, 0), InputMapping.DesktopToAbsolute(-1728, -200, VirtualScreen));
        var (dx, dy) = InputMapping.DesktopToAbsolute(1919, 1079, VirtualScreen);
        Assert.InRange(dx, 65500, 65535);
        Assert.InRange(dy, 65400, 65535);

        // (0,0) of the primary is 1728/3648 and 200/1280 of the way across the virtual screen
        var origin = InputMapping.DesktopToAbsolute(0, 0, VirtualScreen);
        Assert.Equal((31044, 10240), origin);
    }

    [Fact]
    public void Absolute_RoundTripsEveryPixel()
    {
        foreach (var vs in new[] { VirtualScreen, Primary, new DesktopRect(-3840, -1080, 7680, 2160), new DesktopRect(0, 0, 1366, 768) })
        {
            for (var x = vs.Left; x < vs.Right; x++)
            {
                var (dx, _) = InputMapping.DesktopToAbsolute(x, vs.Top, vs);
                Assert.InRange(dx, 0, 65535);
                Assert.Equal(x, InputMapping.AbsoluteToDesktop(dx, 0, vs).X);
            }

            for (var y = vs.Top; y < vs.Bottom; y++)
            {
                var (_, dy) = InputMapping.DesktopToAbsolute(vs.Left, y, vs);
                Assert.Equal(y, InputMapping.AbsoluteToDesktop(0, dy, vs).Y);
            }
        }
    }

    [Fact]
    public void Absolute_ClampsOutsidePoints()
    {
        Assert.Equal((0, 0), InputMapping.DesktopToAbsolute(-5000, -5000, VirtualScreen));
        var (dx, dy) = InputMapping.DesktopToAbsolute(99_999, 99_999, VirtualScreen);
        Assert.Equal(InputMapping.DesktopToAbsolute(1919, 1079, VirtualScreen), (dx, dy));
    }
}

public class InputInjectorTests
{
    private const MouseFlags AbsMove = MouseFlags.Move | MouseFlags.Absolute | MouseFlags.VirtualDesk;
    private static readonly DesktopRect Tablet = new(-1728, -200, 1728, 1080);
    private static readonly DesktopRect VirtualScreen = new(-1728, -200, 3648, 1280);

    private readonly FakeMouse _mouse = new(VirtualScreen);
    private DesktopRect? _output = Tablet;
    private readonly InputInjector _injector;

    public InputInjectorTests()
    {
        _injector = new InputInjector(_mouse, () => _output);
    }

    private static PointerMessage P(PointerAction action, PointerButtons buttons, ushort x = 32768, ushort y = 32768) =>
        new(action, 0, buttons, x, y);

    [Fact]
    public void Tap_IsMoveDownThenUp()
    {
        _injector.OnPointer(P(PointerAction.Down, PointerButtons.Left));
        _injector.OnPointer(P(PointerAction.Up, PointerButtons.None));

        Assert.Equal(2, _mouse.Batches.Count);
        Assert.Equal([AbsMove, MouseFlags.LeftDown], _mouse.Batches[0].Select(i => i.Flags));
        Assert.Equal([AbsMove, MouseFlags.LeftUp], _mouse.Batches[1].Select(i => i.Flags));
        Assert.Equal(PointerButtons.None, _injector.HeldButtons);
    }

    [Fact]
    public void Move_LandsOnTheMappedPixel()
    {
        _injector.OnPointer(P(PointerAction.Down, PointerButtons.None, 0, 0));
        _injector.OnPointer(P(PointerAction.Move, PointerButtons.None, 32768, 32768));
        _injector.OnPointer(P(PointerAction.Move, PointerButtons.None, 65535, 65535));

        var landed = _mouse.Batches.Select(b => InputMapping.AbsoluteToDesktop(b[0].Dx, b[0].Dy, VirtualScreen)).ToList();
        Assert.Equal([(-1728, -200), (-864, 340), (-1, 879)], landed);
    }

    [Fact]
    public void Drag_HoldsLeftUntilUp()
    {
        _injector.OnPointer(P(PointerAction.Down, PointerButtons.Left, 100, 100));
        _injector.OnPointer(P(PointerAction.Move, PointerButtons.Left, 200, 100));
        _injector.OnPointer(P(PointerAction.Move, PointerButtons.Left, 300, 100));
        Assert.Equal(PointerButtons.Left, _injector.HeldButtons);
        Assert.All(_mouse.Batches.Skip(1), b => Assert.Equal([AbsMove], b.Select(i => i.Flags)));

        _injector.OnPointer(P(PointerAction.Up, PointerButtons.None, 300, 100));
        Assert.Equal([AbsMove, MouseFlags.LeftUp], _mouse.Batches[^1].Select(i => i.Flags));
    }

    [Fact]
    public void RightClick()
    {
        _injector.OnPointer(P(PointerAction.Down, PointerButtons.Right));
        _injector.OnPointer(P(PointerAction.Up, PointerButtons.None));
        Assert.Equal([AbsMove, MouseFlags.RightDown], _mouse.Batches[0].Select(i => i.Flags));
        Assert.Equal([AbsMove, MouseFlags.RightUp], _mouse.Batches[1].Select(i => i.Flags));
    }

    [Fact]
    public void Cancel_ReleasesWithoutMoving()
    {
        _injector.OnPointer(P(PointerAction.Down, PointerButtons.Left | PointerButtons.Middle));
        _injector.OnPointer(P(PointerAction.Cancel, PointerButtons.None));
        Assert.Equal([MouseFlags.LeftUp, MouseFlags.MiddleUp], _mouse.Batches[^1].Select(i => i.Flags));
        Assert.Equal(PointerButtons.None, _injector.HeldButtons);

        // Nothing held: cancel and ReleaseAll send nothing
        var count = _mouse.Batches.Count;
        _injector.OnPointer(P(PointerAction.Cancel, PointerButtons.None));
        _injector.ReleaseAll("test");
        Assert.Equal(count, _mouse.Batches.Count);
    }

    [Fact]
    public void ReleaseAll_AfterDisconnectMidDrag()
    {
        _injector.OnPointer(P(PointerAction.Down, PointerButtons.Left));
        _injector.ReleaseAll("disconnect");
        Assert.Equal([MouseFlags.LeftUp], _mouse.Batches[^1].Select(i => i.Flags));
    }

    [Fact]
    public void UnknownOutput_InjectsNothingAndReleases()
    {
        _injector.OnPointer(P(PointerAction.Down, PointerButtons.Left));
        _output = null;
        _injector.OnPointer(P(PointerAction.Move, PointerButtons.Left));
        Assert.Equal([MouseFlags.LeftUp], _mouse.Batches[^1].Select(i => i.Flags));
        _injector.OnPointer(P(PointerAction.Down, PointerButtons.Left));
        Assert.Equal(2, _mouse.Batches.Count);
    }

    [Fact]
    public void Scroll_UsesWheelAndHWheel()
    {
        _injector.OnScroll(new ScrollMessage(-60, 240));
        Assert.Equal([new MouseInput(0, 0, 240, MouseFlags.Wheel), new MouseInput(0, 0, -60, MouseFlags.HWheel)], _mouse.Batches[0]);
        _injector.OnScroll(new ScrollMessage(0, 0));
        Assert.Single(_mouse.Batches);
    }

    internal sealed class FakeMouse(DesktopRect virtualScreen) : IMouseBackend
    {
        public List<MouseInput[]> Batches { get; } = [];

        public DesktopRect VirtualScreen => virtualScreen;

        public int Send(ReadOnlySpan<MouseInput> inputs)
        {
            Batches.Add(inputs.ToArray());
            return inputs.Length;
        }
    }
}

public class SessionInputBridgeTests
{
    private readonly InputInjectorTests.FakeMouse _mouse = new(new DesktopRect(0, 0, 1920, 1080));
    private readonly FakeTransport _transport = new();
    private bool _formatSent;

    public SessionInputBridgeTests()
    {
        var injector = new InputInjector(_mouse, () => new DesktopRect(0, 0, 1920, 1080));
        _ = new SessionInputBridge(_transport, injector, _ => _formatSent);
    }

    private static readonly PointerMessage LeftDown = new(PointerAction.Down, 0, PointerButtons.Left, 10, 10);

    [Fact]
    public void InputBeforeTheVideoFormatIsIgnored()
    {
        var session = _transport.Start(1);
        session.Pointer(LeftDown);
        session.Scroll(new ScrollMessage(0, 120));
        Assert.Empty(_mouse.Batches);

        _formatSent = true;
        session.Pointer(LeftDown);
        Assert.Single(_mouse.Batches);
    }

    [Fact]
    public void DisconnectReleasesHeldButtons()
    {
        _formatSent = true;
        var session = _transport.Start(1);
        session.Pointer(LeftDown);
        _transport.End(session);
        Assert.Equal([MouseFlags.LeftUp], _mouse.Batches[^1].Select(i => i.Flags));
    }

    [Fact]
    public void ReplacedSessionCanNoLongerInject()
    {
        _formatSent = true;
        var old = _transport.Start(1);
        old.Pointer(LeftDown);
        var current = _transport.Start(2); // releases the old session's button
        Assert.Equal([MouseFlags.LeftUp], _mouse.Batches[^1].Select(i => i.Flags));

        var count = _mouse.Batches.Count;
        old.Pointer(LeftDown);
        Assert.Equal(count, _mouse.Batches.Count);

        current.Pointer(LeftDown);
        _transport.End(old); // late end of the replaced session must not release the new session's button
        Assert.Equal([MouseFlags.Move | MouseFlags.Absolute | MouseFlags.VirtualDesk, MouseFlags.LeftDown], _mouse.Batches[^1].Select(i => i.Flags));
    }

    private sealed class FakeTransport : IDisplayTransport
    {
        public event Action<IDisplaySession>? SessionStarted;
        public event Action<IDisplaySession>? SessionEnded;

        public FakeSession Start(int id)
        {
            var s = new FakeSession(id);
            SessionStarted?.Invoke(s);
            return s;
        }

        public void End(FakeSession s) => SessionEnded?.Invoke(s);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession(int id) : IDisplaySession
    {
        public int Id => id;
        public string RemoteDescription => "test";
        public ClientHello Hello => new(1920, 1200, 240, 0);
        public bool IsOpen => true;

        public event Action? KeyframeRequested { add { } remove { } }
        public event Action<ClientStats>? StatsReceived { add { } remove { } }
        public event Action<PointerMessage>? PointerReceived;
        public event Action<ScrollMessage>? ScrollReceived;

        public void Pointer(PointerMessage m) => PointerReceived?.Invoke(m);

        public void Scroll(ScrollMessage m) => ScrollReceived?.Invoke(m);

        public bool SendControl(MessageType type, ReadOnlyMemory<byte> payload) => true;

        public bool SendVideo(ReadOnlyMemory<byte> accessUnit, bool keyframe, ulong captureTimestampUs) => true;

        public void Close(string reason)
        {
        }
    }
}
