using NexScreen.Companion.Net;

namespace NexScreen.Companion.Tests;

public class ProtocolTests
{
    [Fact]
    public void Header_RoundTrips_BigEndian()
    {
        var header = NexLink.BuildHeader(MessageType.Frame, 0x0001, 0x00A1B2C3, 0x1122334455667788);

        Assert.Equal(new byte[] { 0, 0x83, 0x00, 0x01, 0x00, 0xA1, 0xB2, 0xC3, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }, header);
        Assert.Equal(HeaderStatus.Ok, NexLink.TryParseHeader(header, out var parsed));
        Assert.Equal(new MessageHeader(0, MessageType.Frame, 1, 0x00A1B2C3, 0x1122334455667788), parsed);
    }

    [Fact]
    public void Header_RejectsBadVersion()
    {
        var header = NexLink.BuildHeader(MessageType.Hello, 0, 12, 0);
        header[0] = 1;
        Assert.Equal(HeaderStatus.BadVersion, NexLink.TryParseHeader(header, out _));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x02)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void Header_RejectsUnknownType(byte type)
    {
        var header = NexLink.BuildHeader(MessageType.Hello, 0, 0, 0);
        header[1] = type;
        Assert.Equal(HeaderStatus.UnknownType, NexLink.TryParseHeader(header, out _));
    }

    [Fact]
    public void Header_LengthCapIs16MiB()
    {
        var header = NexLink.BuildHeader(MessageType.Frame, 0, NexLink.MaxPayloadLength, 0);
        Assert.Equal(HeaderStatus.Ok, NexLink.TryParseHeader(header, out _));

        header[4] = 0x01;
        header[5] = 0x00;
        header[6] = 0x00;
        header[7] = 0x01; // 16 MiB + 1
        Assert.Equal(HeaderStatus.TooLarge, NexLink.TryParseHeader(header, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => NexLink.BuildHeader(MessageType.Frame, 0, NexLink.MaxPayloadLength + 1, 0));
    }

    [Fact]
    public void Hello_Parses()
    {
        var payload = new byte[] { (byte)'N', (byte)'X', (byte)'S', (byte)'0', 0x07, 0x80, 0x04, 0xB0, 0x00, 0xF0, 0x00, 0x5A };
        var hello = NexLink.ParseHello(payload);
        Assert.Equal(new ClientHello(1920, 1200, 240, 90), hello);
        Assert.Equal(payload, NexLink.BuildHello(hello));
    }

    [Fact]
    public void Hello_RejectsBadMagicRotationAndSize()
    {
        var good = NexLink.BuildHello(new ClientHello(1920, 1200, 240, 0));

        var badMagic = (byte[])good.Clone();
        badMagic[3] = (byte)'1';
        Assert.Throws<ProtocolException>(() => NexLink.ParseHello(badMagic));

        var badRotation = NexLink.BuildHello(new ClientHello(1920, 1200, 240, 45));
        Assert.Throws<ProtocolException>(() => NexLink.ParseHello(badRotation));

        Assert.Throws<ProtocolException>(() => NexLink.ParseHello(good.AsSpan(0, 11)));
    }

    [Fact]
    public void HelloAck_And_Stats_RoundTrip()
    {
        var ack = new HelloAck(1920, 1200, 30, NexLink.CodecH264);
        var bytes = NexLink.BuildHelloAck(ack);
        Assert.Equal(12, bytes.Length);
        Assert.Equal("NXS0"u8.ToArray(), bytes[..4]);
        Assert.Equal(0, bytes[11]);
        Assert.Equal(ack, NexLink.ParseHelloAck(bytes));

        var stats = new ClientStats(100, 2, 4000, 9000);
        Assert.Equal(stats, NexLink.ParseStats(NexLink.BuildStats(stats)));
    }

    [Fact]
    public async Task Reader_HandlesPartialReads()
    {
        var hello = NexLink.BuildHello(new ClientHello(1920, 1200, 240, 0));
        var stats = NexLink.BuildStats(new ClientStats(1, 2, 3, 4));
        var data = Concat(
            NexLink.BuildHeader(MessageType.Hello, 0, (uint)hello.Length, 5), hello,
            NexLink.BuildHeader(MessageType.RequestKeyframe, 0, 0, 6),
            NexLink.BuildHeader(MessageType.Stats, 0, (uint)stats.Length, 7), stats);

        var reader = new NexLinkReader(new TrickleStream(data));
        var m1 = await reader.ReadAsync(CancellationToken.None);
        var m2 = await reader.ReadAsync(CancellationToken.None);
        var m3 = await reader.ReadAsync(CancellationToken.None);
        var end = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(MessageType.Hello, m1!.Header.Type);
        Assert.Equal(5UL, m1.Header.TimestampUs);
        Assert.Equal(hello, m1.Payload);
        Assert.Equal(MessageType.RequestKeyframe, m2!.Header.Type);
        Assert.Empty(m2.Payload);
        Assert.Equal(new ClientStats(1, 2, 3, 4), NexLink.ParseStats(m3!.Payload));
        Assert.Null(end);
    }

    [Fact]
    public async Task Reader_RejectsServerTypesFromClient()
    {
        var data = NexLink.BuildHeader(MessageType.Frame, 1, 0, 0);
        var reader = new NexLinkReader(new MemoryStream(data));
        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_RejectsWrongPayloadLengthBeforeReadingIt()
    {
        // A STATS header announcing 1 MiB must be rejected without allocating or waiting for the payload.
        var data = NexLink.BuildHeader(MessageType.Stats, 0, 1 << 20, 0);
        var reader = new NexLinkReader(new MemoryStream(data));
        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_RejectsBadVersionAndOversizedLength()
    {
        var badVersion = NexLink.BuildHeader(MessageType.Hello, 0, 12, 0);
        badVersion[0] = 2;
        await Assert.ThrowsAsync<ProtocolException>(() => new NexLinkReader(new MemoryStream(badVersion)).ReadAsync(CancellationToken.None));

        var oversized = NexLink.BuildHeader(MessageType.Hello, 0, 0, 0);
        oversized[4] = 0x7F;
        await Assert.ThrowsAsync<ProtocolException>(() => new NexLinkReader(new MemoryStream(oversized)).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_ClientRoleAcceptsServerMessages()
    {
        var payload = new byte[] { 0, 0, 0, 1, 0x65, 0x88 };
        var data = Concat(NexLink.BuildHeader(MessageType.Frame, NexLink.FrameFlagKeyframe, (uint)payload.Length, 9), payload);
        var message = await new NexLinkReader(new TrickleStream(data), serverRole: false).ReadAsync(CancellationToken.None);
        Assert.Equal(MessageType.Frame, message!.Header.Type);
        Assert.Equal(NexLink.FrameFlagKeyframe, message.Header.Flags);
        Assert.Equal(payload, message.Payload);
    }

    [Fact]
    public async Task Reader_TruncatedMessageThrows()
    {
        var hello = NexLink.BuildHello(new ClientHello(1, 1, 1, 0));
        var data = Concat(NexLink.BuildHeader(MessageType.Hello, 0, (uint)hello.Length, 0), hello.AsSpan(0, 5).ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() => new NexLinkReader(new TrickleStream(data)).ReadAsync(CancellationToken.None));

        await Assert.ThrowsAsync<EndOfStreamException>(() => new NexLinkReader(new MemoryStream(new byte[7])).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void Pointer_LayoutAndRoundTrip()
    {
        var pointer = new PointerMessage(PointerAction.Move, 3, PointerButtons.Left, 0x8000, 0xFFFF);
        var bytes = NexLink.BuildPointer(pointer);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x01, 0x80, 0x00, 0xFF, 0xFF }, bytes);
        Assert.Equal(pointer, NexLink.ParsePointer(bytes));
    }

    [Theory]
    [InlineData(new byte[] { 4, 0, 0, 0, 0, 0, 0, 0 })] // action out of range
    [InlineData(new byte[] { 0xFF, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 8, 0, 0, 0, 0 })] // unknown button bit
    [InlineData(new byte[] { 0, 0, 0x80, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0 })] // 7 bytes
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 })] // 9 bytes
    public void Pointer_RejectsInvalid(byte[] payload)
    {
        Assert.Throws<ProtocolException>(() => NexLink.ParsePointer(payload));
    }

    [Fact]
    public void Scroll_IsSignedBigEndian()
    {
        var bytes = NexLink.BuildScroll(new ScrollMessage(-120, 240));
        Assert.Equal(new byte[] { 0xFF, 0x88, 0x00, 0xF0 }, bytes);
        Assert.Equal(new ScrollMessage(-120, 240), NexLink.ParseScroll(bytes));
        Assert.Equal(new ScrollMessage(short.MinValue, short.MaxValue), NexLink.ParseScroll(new byte[] { 0x80, 0, 0x7F, 0xFF }));
        Assert.Throws<ProtocolException>(() => NexLink.ParseScroll(new byte[3]));
    }

    [Fact]
    public async Task Reader_AcceptsInputMessages()
    {
        var pointer = NexLink.BuildPointer(new PointerMessage(PointerAction.Down, 0, PointerButtons.Right, 1, 2));
        var scroll = NexLink.BuildScroll(new ScrollMessage(0, 120));
        var data = Concat(
            NexLink.BuildHeader(MessageType.Pointer, 0, (uint)pointer.Length, 1), pointer,
            NexLink.BuildHeader(MessageType.Scroll, 0, (uint)scroll.Length, 2), scroll);
        var reader = new NexLinkReader(new TrickleStream(data));

        var m1 = await reader.ReadAsync(CancellationToken.None);
        var m2 = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(new PointerMessage(PointerAction.Down, 0, PointerButtons.Right, 1, 2), NexLink.ParsePointer(m1!.Payload));
        Assert.Equal(new ScrollMessage(0, 120), NexLink.ParseScroll(m2!.Payload));
    }

    [Theory]
    [InlineData(MessageType.Pointer, 7u)]
    [InlineData(MessageType.Pointer, 9u)]
    [InlineData(MessageType.Scroll, 0u)]
    [InlineData(MessageType.Scroll, 8u)]
    public async Task Reader_RejectsWrongInputPayloadSize(MessageType type, uint length)
    {
        var data = Concat(NexLink.BuildHeader(type, 0, length, 0), new byte[length]);
        await Assert.ThrowsAsync<ProtocolException>(() => new NexLinkReader(new MemoryStream(data)).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void Input_TypesAreKnownClientToServerTypes()
    {
        Assert.True(NexLink.IsClientToServer(MessageType.Pointer));
        Assert.True(NexLink.IsClientToServer(MessageType.Scroll));
        Assert.Equal(HeaderStatus.Ok, NexLink.TryParseHeader(NexLink.BuildHeader(MessageType.Pointer, 0, 8, 0), out _));
        Assert.Equal(HeaderStatus.Ok, NexLink.TryParseHeader(NexLink.BuildHeader(MessageType.Scroll, 0, 4, 0), out _));
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Returns at most one byte per read, to exercise partial reads.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
