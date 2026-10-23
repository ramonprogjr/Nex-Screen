using NexScreen.Companion.Capture;
using NexScreen.Companion.Displays;
using NexScreen.Companion.Encoding;
using NexScreen.Companion.Util;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NexScreen.Companion.Streaming;

public sealed record PipelineOptions(
    string OutputSelector,
    int Fps,
    int Bitrate,
    int GopFrames,
    int? Width,
    int? Height,
    H264Profile Profile,
    int? Level,
    EncoderPreference Encoder,
    int MaxWidth = 1920,
    int MaxHeight = 1088,
    bool DrawCursor = true)
{
    public (int Width, int Height) EncodeSize(int captureWidth, int captureHeight) =>
        EncodeSizing.Fit(Width, Height, MaxWidth, MaxHeight, captureWidth, captureHeight);
}

public static class EncodeSizing
{
    /// <summary>
    /// Encode size for a capture: the explicit size if given, otherwise the capture size scaled down to fit
    /// maxWidth x maxHeight. Scaled widths are multiples of 16 and the height keeps the aspect ratio
    /// (1920x1200 -> 1728x1080 for the default 1920x1088 limit).
    /// </summary>
    public static (int Width, int Height) Fit(int? width, int? height, int maxWidth, int maxHeight,
        int captureWidth, int captureHeight)
    {
        if (width is int w && height is int h)
        {
            return (w, h);
        }

        var cw = captureWidth & ~1;
        var ch = captureHeight & ~1;
        if (cw <= maxWidth && ch <= maxHeight)
        {
            return (cw, ch);
        }

        var scale = Math.Min((double)maxWidth / cw, (double)maxHeight / ch);
        var ew = Math.Max(16, (int)(cw * scale) & ~15);
        var eh = Math.Min(maxHeight, (int)Math.Round(ew * (double)ch / cw)) & ~1;
        return (ew, eh);
    }
}

/// <summary>
/// Capture -> GPU NV12 conversion -> H.264 encode loop. Runs on a dedicated thread (it may switch
/// the thread's desktop after UAC / lock screen transitions).
/// </summary>
public sealed class StreamPipeline(PipelineOptions options, IEncodedStreamSink sink)
{
    private const double KeepAliveSeconds = 1.0;
    private const int MinRetryMs = 1000;
    private const int MaxRetryMs = 30_000;

    private readonly object _outputGate = new();
    private MftH264Encoder? _encoder;
    private int _encWidth;
    private int _encHeight;
    private byte[]? _config;
    private StreamFormat? _format;
    private volatile bool _pendingKey = true;
    private EncoderPreference _preference = options.Encoder;

    // Statistics (written under _outputGate or by the capture thread only).
    private long _framesOut;
    private long _bytesOut;
    private long _keyframesOut;
    private long _framesSubmitted;
    private long _windowFrames;
    private long _windowBytes;
    private double _windowStart = MonoClock.NowSeconds;
    private double _firstOutputAt = -1;
    private double _lastOutputAt;
    private double _submitSeconds;
    private long _acqNew;
    private long _acqIdle;
    private long _pointerFrames;
    private readonly ManualResetEventSlim _firstFrame = new(false);

    /// <summary>Signaled when the first encoded frame reached the sink.</summary>
    public WaitHandle FirstFrame => _firstFrame.WaitHandle;

    /// <summary>Raised when the selected output appears or disappears (true on the first successful lookup too).</summary>
    public event Action<bool>? OutputAvailabilityChanged;

    public string? EncoderName { get; private set; }
    public bool EncoderIsHardware { get; private set; }
    public long FramesOut => Interlocked.Read(ref _framesOut);
    public long KeyframesOut => Interlocked.Read(ref _keyframesOut);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public long FramesSubmitted => _framesSubmitted;

    /// <summary>Average output FPS between the first and the last encoded frame.</summary>
    public double AverageFps
    {
        get
        {
            lock (_outputGate)
            {
                var span = _lastOutputAt - _firstOutputAt;
                return _firstOutputAt < 0 || span <= 0 ? 0 : (_framesOut - 1) / span;
            }
        }
    }

    public void Run(CancellationToken ct)
    {
        NativeMethods.timeBeginPeriod(1);
        try
        {
            string? lastMissing = null;
            bool? available = null;
            var backoffMs = MinRetryMs;
            while (!ct.IsCancellationRequested)
            {
                DisplayOutputInfo? target;
                try
                {
                    target = DisplayEnumerator.Find(options.OutputSelector);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // DXGI enumeration can fail transiently during display topology changes.
                    Log.Warn($"Output enumeration failed: {ex.GetType().Name}: {ex.Message.Trim()}");
                    target = null;
                }

                if (available != (target is not null))
                {
                    available = target is not null;
                    OutputAvailabilityChanged?.Invoke(available.Value);
                }

                if (target is null)
                {
                    if (lastMissing != options.OutputSelector)
                    {
                        lastMissing = options.OutputSelector;
                        Log.Warn($"Output \"{options.OutputSelector}\" not found; waiting for it (use --list to see outputs).");
                    }

                    ct.WaitHandle.WaitOne(2000);
                    continue;
                }

                lastMissing = null;
                var startedAt = MonoClock.NowSeconds;
                try
                {
                    RunOnOutput(target, ct);
                    backoffMs = MinRetryMs;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Repeated failures (e.g. no usable encoder) back off up to 30 s; a long healthy run resets it.
                    if (MonoClock.NowSeconds - startedAt > 60)
                    {
                        backoffMs = MinRetryMs;
                    }

                    Log.Error($"Capture pipeline error: {ex.GetType().Name}: {ex.Message.Trim()}; restarting in {backoffMs / 1000.0:0.#} s.");
                    ct.WaitHandle.WaitOne(backoffMs);
                    backoffMs = Math.Min(backoffMs * 2, MaxRetryMs);
                }
            }
        }
        finally
        {
            DisposeEncoder();
            NativeMethods.timeEndPeriod(1);
        }
    }

    private void RunOnOutput(DisplayOutputInfo target, CancellationToken ct)
    {
        using var gpu = GpuDevice.Create(target.AdapterIndex);
        using var duplicator = new DesktopDuplicator(gpu, target.OutputIndex) { DrawCursor = options.DrawCursor };
        Log.Info($"Capturing {target.GdiName} \"{target.FriendlyName}\" {target.Width}x{target.Height} on \"{gpu.AdapterName}\"");

        Nv12Converter? converter = null;
        var started = false;
        var unavailableSince = -1.0;
        string? lastStartError = null;
        var interval = 1.0 / options.Fps;
        var nextDue = 0.0;
        var lastSubmit = 0.0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!started)
                {
                    if (!duplicator.TryStart(out var error))
                    {
                        // Secure desktop (UAC / lock screen), mode change in progress, or output gone.
                        if (error != lastStartError)
                        {
                            Log.Warn($"Desktop duplication unavailable ({error}); retrying.");
                            lastStartError = error;
                        }

                        if (unavailableSince < 0)
                        {
                            unavailableSince = MonoClock.NowSeconds;
                        }
                        else if (MonoClock.NowSeconds - unavailableSince > 5 && DisplayEnumerator.Find(options.OutputSelector)?.GdiName != target.GdiName)
                        {
                            Log.Warn("Output changed; re-resolving.");
                            return;
                        }

                        ct.WaitHandle.WaitOne(250);
                        continue;
                    }

                    started = true;
                    unavailableSince = -1;
                    lastStartError = null;
                    _pendingKey = true;
                    Log.Info($"Desktop duplication started: {duplicator.Width}x{duplicator.Height}, rotation {duplicator.Rotation}, pointer {(duplicator.DrawCursor ? "drawn" : "not drawn (--no-cursor)")}");
                    if (duplicator.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                    {
                        Log.Warn("Rotated outputs are captured in scan-out orientation (not handled in M2); the pointer is not drawn.");
                    }
                }

                if (!sink.WantsFrames)
                {
                    ct.WaitHandle.WaitOne(100);
                    continue;
                }

                if (sink.TakeKeyframeRequest())
                {
                    _pendingKey = true;
                }

                // (Re)build converter and encoder on first use, size change or encoder failure.
                var (encW, encH) = options.EncodeSize(duplicator.Width, duplicator.Height);
                if (converter is null || converter.InputWidth != duplicator.Width || converter.InputHeight != duplicator.Height ||
                    converter.OutputWidth != encW || converter.OutputHeight != encH)
                {
                    converter?.Dispose();
                    converter = new Nv12Converter(gpu, duplicator.Width, duplicator.Height, encW, encH);
                    Log.Info($"Converter: {duplicator.Width}x{duplicator.Height} BGRA -> {encW}x{encH} NV12 (GPU video processor)");
                }

                if (_encoder is null || _encWidth != encW || _encHeight != encH || _encoder.Failed)
                {
                    if (_encoder?.Failed == true && _encoder.IsHardware && _preference == EncoderPreference.Auto)
                    {
                        Log.Warn("Hardware encoder failed at runtime; switching to the software encoder.");
                        _preference = EncoderPreference.Software;
                    }

                    CreateEncoder(gpu, encW, encH);
                }

                // Frame pacing: never faster than --fps.
                var now = MonoClock.NowSeconds;
                if (now < nextDue)
                {
                    Thread.Sleep(Math.Max(1, (int)((nextDue - now) * 1000)));
                    continue;
                }

                var untilKeepAlive = (lastSubmit + KeepAliveSeconds) - now;
                var timeoutMs = _pendingKey && converter.Last is not null ? 0 : (int)Math.Clamp(untilKeepAlive * 1000, 0, 100);
                var result = duplicator.Acquire(timeoutMs);
                now = MonoClock.NowSeconds;
                if (result == AcquireResult.NewFrame)
                {
                    // Desktop updates and, with the pointer drawn, pointer moves / shape changes (paced by --fps above).
                    _acqNew++;
                    _pointerFrames = duplicator.PointerFrames;
                }
                else
                {
                    _acqIdle++;
                }

                switch (result)
                {
                    case AcquireResult.NewFrame:
                    {
                        var nv12 = converter.Convert(duplicator.Frame!);
                        if (Submit(nv12, duplicator.FrameTimestampUs))
                        {
                            lastSubmit = now;
                            nextDue = Math.Max(nextDue + interval, now - interval);
                        }

                        break;
                    }

                    case AcquireResult.AccessLost:
                        Log.Info("Desktop duplication access lost (mode change or desktop switch); recreating.");
                        started = false;
                        break;

                    default:
                        // Nothing new on screen: re-encode the last image for keyframe requests and keep-alive.
                        if (converter.Last is not null && (_pendingKey || now - lastSubmit >= KeepAliveSeconds))
                        {
                            if (Submit(converter.Last, MonoClock.NowMicroseconds))
                            {
                                lastSubmit = now;
                                nextDue = now + interval;
                            }
                        }

                        break;
                }

                LogWindowStats();
            }
        }
        finally
        {
            DisposeEncoder();
            converter?.Dispose();
        }
    }

    private bool Submit(ID3D11Texture2D nv12, ulong captureUs)
    {
        var force = _pendingKey;
        if (force)
        {
            _pendingKey = false;
        }

        var t0 = MonoClock.NowSeconds;
        var ok = _encoder!.Encode(nv12, (long)captureUs * 10, force);
        _submitSeconds += MonoClock.NowSeconds - t0;
        if (ok)
        {
            _framesSubmitted++;
            return true;
        }

        if (force)
        {
            _pendingKey = true;
        }

        return false;
    }

    private void CreateEncoder(GpuDevice gpu, int width, int height)
    {
        DisposeEncoder();
        var settings = new EncoderSettings(width, height, options.Fps, options.Bitrate, options.GopFrames, options.Profile, options.Level, _preference);
        lock (_outputGate)
        {
            _config = null;
            _encWidth = width;
            _encHeight = height;
        }

        _encoder = MftH264Encoder.Create(gpu, settings, OnEncoderOutput);
        EncoderName = _encoder.Name;
        EncoderIsHardware = _encoder.IsHardware;
        _pendingKey = true;
        Log.Info($"Encoding {width}x{height} @ {options.Fps} fps max, {options.Bitrate / 1000} kbps CBR, GOP {options.GopFrames}, {options.Profile} profile");
    }

    private void DisposeEncoder()
    {
        _encoder?.Dispose();
        _encoder = null;
    }

    private void OnEncoderOutput(EncodedSample sample)
    {
        EncodedFrame frame;
        lock (_outputGate)
        {
            var idr = AnnexB.ContainsIdr(sample.Data);
            if (idr)
            {
                var ps = AnnexB.ExtractParameterSets(sample.Data);
                if (ps is not null)
                {
                    _config = ps;
                }
            }

            _config ??= _encoder?.GetSequenceHeader();
            if (_config is null)
            {
                Log.Warn("Encoder output without SPS/PPS yet; dropping and requesting a keyframe.");
                _pendingKey = true;
                return;
            }

            if (_format is null || _format.Width != _encWidth || _format.Height != _encHeight ||
                !_format.CodecConfig.AsSpan().SequenceEqual(_config))
            {
                _format = new StreamFormat(_encWidth, _encHeight, options.Fps, _config);
                var pl = AnnexB.ReadProfileLevel(_config);
                if (pl is { } v)
                {
                    Log.Info($"Codec config: H.264 {AnnexB.ProfileName(v.Profile)} level {v.Level / 10.0:0.0}, {_config.Length} bytes SPS/PPS");
                    if (options.Level is { } wanted && wanted != v.Level)
                    {
                        Log.Warn($"Encoder chose level {v.Level / 10.0:0.0} instead of the requested {wanted / 10.0:0.0} (frame size or rate needs it).");
                    }
                }
            }

            var payload = AnnexB.RemoveParameterSets(sample.Data);
            frame = new EncodedFrame(payload, idr, (ulong)Math.Max(0, sample.SampleTime100ns / 10), _format);

            var now = MonoClock.NowSeconds;
            if (_firstOutputAt < 0)
            {
                _firstOutputAt = now;
            }

            _lastOutputAt = now;
            _framesOut++;
            _bytesOut += payload.Length;
            _windowFrames++;
            _windowBytes += payload.Length;
            if (idr)
            {
                _keyframesOut++;
            }
        }

        sink.OnFrame(frame);
        _firstFrame.Set();
    }

    private void LogWindowStats()
    {
        long frames, bytes, acqNew, acqIdle;
        double seconds;
        lock (_outputGate)
        {
            seconds = MonoClock.NowSeconds - _windowStart;
            if (seconds < 5)
            {
                return;
            }

            frames = _windowFrames;
            bytes = _windowBytes;
            _windowFrames = 0;
            _windowBytes = 0;
            _windowStart = MonoClock.NowSeconds;
            acqNew = _acqNew;
            acqIdle = _acqIdle;
            _acqNew = 0;
            _acqIdle = 0;
        }

        Log.Info($"Video: {frames / seconds:0.0} fps, {bytes * 8 / seconds / 1000:0} kbps, encoder dropped {_encoder?.DroppedInputs ?? 0}, total frames {FramesOut}");
        Log.Debug($"Capture: {acqNew} new frames (pointer-only total {_pointerFrames}), {acqIdle} idle polls in {seconds:0.0} s, " +
                  $"encoder submit time {_submitSeconds * 1000 / Math.Max(1, acqNew):0.0} ms/frame");
        _submitSeconds = 0;
    }
}
