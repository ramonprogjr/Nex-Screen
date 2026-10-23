namespace NexScreen.Companion.Net;

public sealed record NexLinkMessage(MessageHeader Header, byte[] Payload);

/// <summary>
/// Reads framed NexLink messages from a stream, tolerating partial reads.
/// Server role: only client-to-server types with the exact payload size are accepted;
/// anything else raises <see cref="ProtocolException"/> so the caller closes the connection.
/// </summary>
public sealed class NexLinkReader(Stream stream, bool serverRole = true)
{
    private readonly byte[] _header = new byte[NexLink.HeaderSize];

    /// <summary>Returns the next message, or null on a clean end of stream at a message boundary.</summary>
    public async Task<NexLinkMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        var got = await ReadFullyAsync(_header, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < NexLink.HeaderSize)
        {
            throw new EndOfStreamException("Connection closed in the middle of a header.");
        }

        var status = NexLink.TryParseHeader(_header, out var header);
        switch (status)
        {
            case HeaderStatus.BadVersion:
                throw new ProtocolException($"Unsupported protocol version {header.Version}.");
            case HeaderStatus.UnknownType:
                throw new ProtocolException($"Unknown message type 0x{(byte)header.Type:X2}.");
            case HeaderStatus.TooLarge:
                throw new ProtocolException($"Payload length {header.Length} exceeds the 16 MiB limit.");
        }

        if (serverRole)
        {
            var expected = NexLink.ExpectedClientPayloadLength(header.Type)
                ?? throw new ProtocolException($"Message type 0x{(byte)header.Type:X2} is not valid from a client.");
            if (header.Length != expected)
            {
                throw new ProtocolException($"{header.Type} payload must be {expected} bytes, got {header.Length}.");
            }
        }

        var payload = header.Length == 0 ? Array.Empty<byte>() : new byte[header.Length];
        if (payload.Length > 0 && await ReadFullyAsync(payload, cancellationToken).ConfigureAwait(false) < payload.Length)
        {
            throw new EndOfStreamException("Connection closed in the middle of a payload.");
        }

        return new NexLinkMessage(header, payload);
    }

    private async Task<int> ReadFullyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            offset += n;
        }

        return offset;
    }
}
