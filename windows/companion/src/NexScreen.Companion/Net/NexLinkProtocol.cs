using System.Buffers.Binary;

namespace NexScreen.Companion.Net;

/// <summary>NexLink v0-usb message types (protocol/nexlink-v0-usb.md).</summary>
public enum MessageType : byte
{
    Hello = 0x01,
    RequestKeyframe = 0x10,
    Stats = 0x11,
    Pointer = 0x20,
    Scroll = 0x21,
    HelloAck = 0x81,
    CodecConfig = 0x82,
    Frame = 0x83,
}

public readonly record struct MessageHeader(byte Version, MessageType Type, ushort Flags, uint Length, ulong TimestampUs);

public enum HeaderStatus
{
    Ok,
    BadVersion,
    UnknownType,
    TooLarge,
    BadLength,
}

public sealed record ClientHello(ushort ScreenWidth, ushort ScreenHeight, ushort DensityDpi, ushort Rotation);

public sealed record ClientStats(uint FramesDecoded, uint FramesDropped, uint DecodeLatencyP50Us, uint DecodeLatencyP95Us);

public sealed record HelloAck(ushort VideoWidth, ushort VideoHeight, ushort Fps, byte Codec);

public enum PointerAction : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}

[Flags]
public enum PointerButtons : ushort
{
    None = 0,
    Left = 0x1,
    Right = 0x2,
    Middle = 0x4,
}

/// <summary>
/// POINTER: X/Y are 0..65535 across the video the client displays (letterbox excluded), 0,0 = top-left.
/// <see cref="Buttons"/> is the button state after the event.
/// </summary>
public readonly record struct PointerMessage(PointerAction Action, byte PointerId, PointerButtons Buttons, ushort X, ushort Y);

/// <summary>SCROLL in wheel units (120 = one notch). Positive Dy scrolls up, positive Dx scrolls right (like WHEEL/HWHEEL).</summary>
public readonly record struct ScrollMessage(short Dx, short Dy);

public sealed class ProtocolException(string message) : Exception(message);

public static class NexLink
{
    public const int HeaderSize = 16;
    public const byte Version = 0;
    public const uint MaxPayloadLength = 16 * 1024 * 1024;
    public const int DefaultPort = 27183;
    public const ushort FrameFlagKeyframe = 0x0001;
    public const byte CodecH264 = 1;

    public const int HelloPayloadSize = 12;
    public const int StatsPayloadSize = 16;
    public const int HelloAckPayloadSize = 12;
    public const int PointerPayloadSize = 8;
    public const int ScrollPayloadSize = 4;
    public const PointerButtons KnownButtons = PointerButtons.Left | PointerButtons.Right | PointerButtons.Middle;

    public static ReadOnlySpan<byte> Magic => "NXS0"u8;

    public static bool IsKnownType(byte type) => type is
        (byte)MessageType.Hello or (byte)MessageType.RequestKeyframe or (byte)MessageType.Stats or
        (byte)MessageType.Pointer or (byte)MessageType.Scroll or
        (byte)MessageType.HelloAck or (byte)MessageType.CodecConfig or (byte)MessageType.Frame;

    public static bool IsClientToServer(MessageType type) =>
        type is MessageType.Hello or MessageType.RequestKeyframe or MessageType.Stats or
            MessageType.Pointer or MessageType.Scroll;

    public static void WriteHeader(Span<byte> destination, MessageType type, ushort flags, uint length, ulong timestampUs)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException("Destination too small for a NexLink header.", nameof(destination));
        }

        if (length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Payload exceeds the 16 MiB NexLink limit.");
        }

        destination[0] = Version;
        destination[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], flags);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], length);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], timestampUs);
    }

    public static byte[] BuildHeader(MessageType type, ushort flags, uint length, ulong timestampUs)
    {
        var header = new byte[HeaderSize];
        WriteHeader(header, type, flags, length, timestampUs);
        return header;
    }

    /// <summary>Parses and validates the generic header fields (version, type, length cap).</summary>
    public static HeaderStatus TryParseHeader(ReadOnlySpan<byte> source, out MessageHeader header)
    {
        if (source.Length < HeaderSize)
        {
            throw new ArgumentException("Source shorter than a NexLink header.", nameof(source));
        }

        header = new MessageHeader(
            source[0],
            (MessageType)source[1],
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]));

        if (header.Version != Version)
        {
            return HeaderStatus.BadVersion;
        }

        if (!IsKnownType(source[1]))
        {
            return HeaderStatus.UnknownType;
        }

        if (header.Length > MaxPayloadLength)
        {
            return HeaderStatus.TooLarge;
        }

        return HeaderStatus.Ok;
    }

    /// <summary>
    /// Payload size a client-to-server message must have, or null when the type is not accepted from a client.
    /// </summary>
    public static int? ExpectedClientPayloadLength(MessageType type) => type switch
    {
        MessageType.Hello => HelloPayloadSize,
        MessageType.RequestKeyframe => 0,
        MessageType.Stats => StatsPayloadSize,
        MessageType.Pointer => PointerPayloadSize,
        MessageType.Scroll => ScrollPayloadSize,
        _ => null,
    };

    public static ClientHello ParseHello(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != HelloPayloadSize)
        {
            throw new ProtocolException($"HELLO payload must be {HelloPayloadSize} bytes, got {payload.Length}.");
        }

        if (!payload[..4].SequenceEqual(Magic))
        {
            throw new ProtocolException("HELLO magic mismatch.");
        }

        var hello = new ClientHello(
            BinaryPrimitives.ReadUInt16BigEndian(payload[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[10..]));

        if (hello.Rotation is not (0 or 90 or 180 or 270))
        {
            throw new ProtocolException($"HELLO rotation {hello.Rotation} is not 0/90/180/270.");
        }

        return hello;
    }

    public static byte[] BuildHello(ClientHello hello)
    {
        var payload = new byte[HelloPayloadSize];
        Magic.CopyTo(payload);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), hello.ScreenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), hello.ScreenHeight);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8), hello.DensityDpi);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10), hello.Rotation);
        return payload;
    }

    public static ClientStats ParseStats(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != StatsPayloadSize)
        {
            throw new ProtocolException($"STATS payload must be {StatsPayloadSize} bytes, got {payload.Length}.");
        }

        return new ClientStats(
            BinaryPrimitives.ReadUInt32BigEndian(payload),
            BinaryPrimitives.ReadUInt32BigEndian(payload[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[12..]));
    }

    public static byte[] BuildStats(ClientStats stats)
    {
        var payload = new byte[StatsPayloadSize];
        BinaryPrimitives.WriteUInt32BigEndian(payload, stats.FramesDecoded);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), stats.FramesDropped);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), stats.DecodeLatencyP50Us);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12), stats.DecodeLatencyP95Us);
        return payload;
    }

    /// <summary>Parses POINTER, rejecting a wrong size, an action above 3 or unknown button bits.</summary>
    public static PointerMessage ParsePointer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PointerPayloadSize)
        {
            throw new ProtocolException($"POINTER payload must be {PointerPayloadSize} bytes, got {payload.Length}.");
        }

        if (payload[0] > (byte)PointerAction.Cancel)
        {
            throw new ProtocolException($"POINTER action {payload[0]} is not 0..3.");
        }

        var buttons = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        if ((buttons & ~(ushort)KnownButtons) != 0)
        {
            throw new ProtocolException($"POINTER buttons 0x{buttons:X4} has unknown bits.");
        }

        return new PointerMessage(
            (PointerAction)payload[0],
            payload[1],
            (PointerButtons)buttons,
            BinaryPrimitives.ReadUInt16BigEndian(payload[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[6..]));
    }

    public static byte[] BuildPointer(PointerMessage pointer)
    {
        var payload = new byte[PointerPayloadSize];
        payload[0] = (byte)pointer.Action;
        payload[1] = pointer.PointerId;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), (ushort)pointer.Buttons);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), pointer.X);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), pointer.Y);
        return payload;
    }

    public static ScrollMessage ParseScroll(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != ScrollPayloadSize)
        {
            throw new ProtocolException($"SCROLL payload must be {ScrollPayloadSize} bytes, got {payload.Length}.");
        }

        return new ScrollMessage(BinaryPrimitives.ReadInt16BigEndian(payload), BinaryPrimitives.ReadInt16BigEndian(payload[2..]));
    }

    public static byte[] BuildScroll(ScrollMessage scroll)
    {
        var payload = new byte[ScrollPayloadSize];
        BinaryPrimitives.WriteInt16BigEndian(payload, scroll.Dx);
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(2), scroll.Dy);
        return payload;
    }

    public static byte[] BuildHelloAck(HelloAck ack)
    {
        var payload = new byte[HelloAckPayloadSize];
        Magic.CopyTo(payload);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), ack.VideoWidth);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), ack.VideoHeight);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8), ack.Fps);
        payload[10] = ack.Codec;
        payload[11] = 0;
        return payload;
    }

    public static HelloAck ParseHelloAck(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != HelloAckPayloadSize || !payload[..4].SequenceEqual(Magic))
        {
            throw new ProtocolException("Malformed HELLO_ACK.");
        }

        return new HelloAck(
            BinaryPrimitives.ReadUInt16BigEndian(payload[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[8..]),
            payload[10]);
    }
}
