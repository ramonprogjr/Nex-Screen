using NexScreen.Companion.Net;
using NexScreen.Companion.Util;

namespace NexScreen.Companion.Streaming;

/// <summary>
/// Bridges the encoder to the active transport session: announces HELLO_ACK + CODEC_CONFIG before the
/// first keyframe and again whenever the stream format changes, drops frames until a keyframe after
/// congestion, and turns REQUEST_KEYFRAME into an IDR request for the pipeline.
/// </summary>
public sealed class NetworkStreamSink : IEncodedStreamSink
{
    private readonly object _gate = new();
    private IDisplaySession? _session;
    private StreamFormat? _announced;
    private bool _waitForKeyframe;
    private bool _keyRequested;
    private long _sentFrames;
    private long _droppedFrames;
    private double _lastCongestionKeyRequest = double.NegativeInfinity;

    // IDRs are large; asking for one on every dropped frame would feed the congestion.
    private const double CongestionKeyRequestInterval = 1.0;

    public NetworkStreamSink(IDisplayTransport transport)
    {
        transport.SessionStarted += OnSessionStarted;
        transport.SessionEnded += OnSessionEnded;
    }

    public bool WantsFrames
    {
        get
        {
            lock (_gate)
            {
                return _session is { IsOpen: true };
            }
        }
    }

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
            var session = _session;
            if (session is null || !session.IsOpen)
            {
                return;
            }

            if (!frame.Format.SameAs(_announced))
            {
                if (!frame.IsKeyframe)
                {
                    _keyRequested = true;
                    _droppedFrames++;
                    return;
                }

                var f = frame.Format;
                session.SendControl(MessageType.HelloAck, NexLink.BuildHelloAck(
                    new HelloAck((ushort)f.Width, (ushort)f.Height, (ushort)f.Fps, NexLink.CodecH264)));
                session.SendControl(MessageType.CodecConfig, f.CodecConfig);
                _announced = f;
                Log.Info($"Client #{session.Id}: sent HELLO_ACK {f.Width}x{f.Height}@{f.Fps} + CODEC_CONFIG ({f.CodecConfig.Length} bytes)");
            }

            if (_waitForKeyframe && !frame.IsKeyframe)
            {
                _droppedFrames++;
                RequestKeyframeAfterCongestion();
                return;
            }

            if (session.SendVideo(frame.Data, frame.IsKeyframe, frame.CaptureTimestampUs))
            {
                _sentFrames++;
                if (frame.IsKeyframe)
                {
                    _waitForKeyframe = false;
                }
            }
            else
            {
                _droppedFrames++;
                _waitForKeyframe = true;
                RequestKeyframeAfterCongestion();
            }
        }
    }

    private void RequestKeyframeAfterCongestion()
    {
        var now = MonoClock.NowSeconds;
        if (now - _lastCongestionKeyRequest >= CongestionKeyRequestInterval)
        {
            _lastCongestionKeyRequest = now;
            _keyRequested = true;
        }
    }

    private void OnSessionStarted(IDisplaySession session)
    {
        lock (_gate)
        {
            _session = session;
            _announced = null;
            _waitForKeyframe = true;
            _keyRequested = true;
            _sentFrames = 0;
            _droppedFrames = 0;
        }

        session.KeyframeRequested += () =>
        {
            Log.Info($"Client #{session.Id}: REQUEST_KEYFRAME");
            lock (_gate)
            {
                if (_session == session)
                {
                    _keyRequested = true;
                }
            }
        };
        session.StatsReceived += stats =>
        {
            long sent, dropped;
            lock (_gate)
            {
                sent = _sentFrames;
                dropped = _droppedFrames;
            }

            Log.Info($"Client #{session.Id} STATS: decoded {stats.FramesDecoded}, dropped {stats.FramesDropped}, " +
                     $"decode p50 {stats.DecodeLatencyP50Us / 1000.0:0.0} ms, p95 {stats.DecodeLatencyP95Us / 1000.0:0.0} ms " +
                     $"(server sent {sent}, skipped {dropped})");
        };
    }

    private void OnSessionEnded(IDisplaySession session)
    {
        lock (_gate)
        {
            if (_session == session)
            {
                _session = null;
                _announced = null;
            }
        }
    }
}
