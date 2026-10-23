namespace NexScreen.Companion.Streaming;

/// <summary>What the client must be told before decoding: HELLO_ACK fields plus CODEC_CONFIG (SPS/PPS, Annex-B).</summary>
public sealed class StreamFormat(int width, int height, int fps, byte[] codecConfig)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Fps { get; } = fps;
    public byte[] CodecConfig { get; } = codecConfig;

    public bool SameAs(StreamFormat? other) =>
        other is not null && other.Width == Width && other.Height == Height && other.Fps == Fps &&
        other.CodecConfig.AsSpan().SequenceEqual(CodecConfig);
}

/// <summary>One encoded access unit without SPS/PPS (they travel in CODEC_CONFIG).</summary>
public sealed record EncodedFrame(byte[] Data, bool IsKeyframe, ulong CaptureTimestampUs, StreamFormat Format);

/// <summary>Consumer of the encoded stream (network session or file dump).</summary>
public interface IEncodedStreamSink
{
    /// <summary>False while nobody needs frames; the pipeline then idles.</summary>
    bool WantsFrames { get; }

    /// <summary>Returns true (once) when the sink needs the next frame to be an IDR.</summary>
    bool TakeKeyframeRequest();

    /// <summary>Called from encoder threads; implementations must be thread-safe and must not block for long.</summary>
    void OnFrame(EncodedFrame frame);
}
