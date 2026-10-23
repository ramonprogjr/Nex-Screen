using NexScreen.Companion.Util;

namespace NexScreen.Companion.Streaming;

/// <summary>Writes the elementary stream (CODEC_CONFIG + FRAMEs) to a .h264 file, starting at a keyframe.</summary>
public sealed class AnnexBFileSink(string path) : IEncodedStreamSink, IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _file = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    private StreamFormat? _written;
    private bool _keyRequested = true;

    public long Frames { get; private set; }
    public long Keyframes { get; private set; }
    public long Bytes { get; private set; }

    public bool WantsFrames => true;

    public bool TakeKeyframeRequest()
    {
        lock (_gate)
        {
            var r = _keyRequested;
            _keyRequested = false;
            return r;
        }
    }

    public void OnFrame(EncodedFrame frame)
    {
        lock (_gate)
        {
            if (!frame.Format.SameAs(_written))
            {
                if (!frame.IsKeyframe)
                {
                    _keyRequested = true;
                    return;
                }

                _file.Write(frame.Format.CodecConfig);
                Bytes += frame.Format.CodecConfig.Length;
                _written = frame.Format;
                Log.Info($"Dump: codec config {frame.Format.Width}x{frame.Format.Height}, {frame.Format.CodecConfig.Length} bytes");
            }

            _file.Write(frame.Data);
            Bytes += frame.Data.Length;
            Frames++;
            if (frame.IsKeyframe)
            {
                Keyframes++;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file.Dispose();
        }
    }
}
