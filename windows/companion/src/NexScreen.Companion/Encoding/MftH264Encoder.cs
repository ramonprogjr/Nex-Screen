using System.Runtime.InteropServices;
using NexScreen.Companion.Capture;
using NexScreen.Companion.Util;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace NexScreen.Companion.Encoding;

public enum EncoderPreference
{
    Auto,
    Hardware,
    Software,
}

public enum H264Profile
{
    Main = 77,
    High = 100,
}

public sealed record EncoderSettings(
    int Width,
    int Height,
    int Fps,
    int Bitrate,
    int GopFrames,
    H264Profile Profile,
    int? Level,
    EncoderPreference Preference);

/// <summary>Raw encoder output: one Annex-B access unit.</summary>
public sealed record EncodedSample(byte[] Data, long SampleTime100ns);

/// <summary>
/// H.264 encoding through a Media Foundation transform. Hardware MFTs are asynchronous
/// (METransformNeedInput / METransformHaveOutput) and take D3D11 NV12 textures directly;
/// the Microsoft software MFT is synchronous and gets NV12 copied to system memory.
/// </summary>
public sealed class MftH264Encoder : IDisposable
{
    private const uint MftEnumFlagSyncMft = 0x1;
    private const uint MftEnumFlagHardware = 0x4;
    private const uint MftEnumFlagLocalMft = 0x10;
    private const uint MftEnumFlagSortAndFilter = 0x40;
    private const int MftOutputStreamProvidesSamples = 0x100;

    private static readonly Guid MftEnumAdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private readonly GpuDevice _gpu;
    private readonly EncoderSettings _settings;
    private readonly Action<EncodedSample> _onOutput;
    private readonly IMFActivate? _activate;
    private readonly IMFTransform _mft;
    private readonly CodecApi? _codecApi;
    private readonly bool _async;
    private readonly bool _providesSamples;
    private readonly int _outputBufferSize;
    private readonly IMFMediaEventGenerator? _events;
    private readonly Thread? _pump;
    private readonly SemaphoreSlim _needInput = new(0, int.MaxValue);
    private readonly object _outputLock = new();
    private ID3D11Texture2D? _staging;
    private volatile bool _disposing;
    private bool _forceKeyWarned;

    public string Name { get; }
    public bool IsHardware { get; }
    public int Width => _settings.Width;
    public int Height => _settings.Height;

    /// <summary>Set when the transform reported a fatal error; the caller should recreate the encoder.</summary>
    public bool Failed { get; private set; }

    public long DroppedInputs { get; private set; }

    private MftH264Encoder(GpuDevice gpu, EncoderSettings settings, Action<EncodedSample> onOutput,
        IMFActivate? activate, IMFTransform mft, string name, bool hardware)
    {
        _gpu = gpu;
        _settings = settings;
        _onOutput = onOutput;
        _activate = activate;
        _mft = mft;
        Name = name;
        IsHardware = hardware;

        var attributes = mft.Attributes;
        _async = MediaFactory.MFGetAttributeUInt32(attributes, TransformAttributeKeys.TransformAsync, 0) != 0;
        if (_async)
        {
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        }

        if (hardware)
        {
            if (MediaFactory.MFGetAttributeUInt32(attributes, TransformAttributeKeys.D3D11Aware, 0) == 0)
            {
                throw new NotSupportedException("MFT is not D3D11 aware.");
            }

            mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)gpu.DeviceManager.NativePointer);
        }

        TrySetAttribute(attributes, MfLowLatency, 1u);

        _codecApi = CodecApi.TryGet(mft);
        ApplyCodecSettings();

        using (var outputType = CreateOutputType())
        {
            mft.SetOutputType(0, outputType, 0);
        }

        using (var inputType = CreateInputType())
        {
            mft.SetInputType(0, inputType, 0);
        }

        var info = mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & MftOutputStreamProvidesSamples) != 0;
        _outputBufferSize = Math.Max(info.Size, settings.Width * settings.Height * 3 / 2);

        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        if (_async)
        {
            _events = mft.QueryInterface<IMFMediaEventGenerator>();
            _pump = new Thread(PumpEvents) { IsBackground = true, Name = "MFT events" };
            _pump.Start();
        }
    }

    public static MftH264Encoder Create(GpuDevice gpu, EncoderSettings settings, Action<EncodedSample> onOutput)
    {
        var errors = new List<string>();
        if (settings.Preference != EncoderPreference.Software)
        {
            foreach (var activate in EnumerateHardware(gpu.AdapterLuid))
            {
                var name = FriendlyName(activate);
                IMFTransform? mft = null;
                try
                {
                    mft = activate.ActivateObject<IMFTransform>();
                    var encoder = new MftH264Encoder(gpu, settings, onOutput, activate, mft, name, true);
                    Log.Info($"Encoder: hardware MFT \"{name}\" ({VendorId(activate)}), async={encoder._async}");
                    return encoder;
                }
                catch (Exception ex) when (ex is SharpGenException or NotSupportedException or COMException)
                {
                    errors.Add($"{name}: {ex.Message.Trim()}");
                    Log.Warn($"Hardware MFT \"{name}\" rejected: {ex.Message.Trim()}");
                    mft?.Dispose();
                    activate.ShutdownObject();
                    activate.Dispose();
                }
            }

            if (settings.Preference == EncoderPreference.Hardware)
            {
                throw new InvalidOperationException("No usable hardware H.264 encoder MFT. " + string.Join("; ", errors));
            }

            Log.Warn("No usable hardware H.264 encoder; falling back to the Microsoft software encoder.");
        }

        foreach (var activate in Enumerate(MftEnumFlagSyncMft | MftEnumFlagLocalMft | MftEnumFlagSortAndFilter, null))
        {
            var name = FriendlyName(activate);
            IMFTransform? mft = null;
            try
            {
                mft = activate.ActivateObject<IMFTransform>();
                var encoder = new MftH264Encoder(gpu, settings, onOutput, activate, mft, name, false);
                Log.Info($"Encoder: software MFT \"{name}\" (system-memory NV12 path)");
                return encoder;
            }
            catch (Exception ex) when (ex is SharpGenException or NotSupportedException or COMException)
            {
                errors.Add($"{name}: {ex.Message.Trim()}");
                mft?.Dispose();
                activate.ShutdownObject();
                activate.Dispose();
            }
        }

        throw new InvalidOperationException("No usable H.264 encoder MFT. " + string.Join("; ", errors));
    }

    /// <summary>Lists available H.264 encoder MFTs (for diagnostics).</summary>
    public static List<string> Describe(long adapterLuid)
    {
        var list = new List<string>();
        foreach (var a in EnumerateHardware(adapterLuid))
        {
            list.Add($"hardware: {FriendlyName(a)} ({VendorId(a)})");
            a.Dispose();
        }

        foreach (var a in Enumerate(MftEnumFlagSyncMft | MftEnumFlagLocalMft | MftEnumFlagSortAndFilter, null))
        {
            list.Add($"software: {FriendlyName(a)}");
            a.Dispose();
        }

        return list;
    }

    /// <summary>Submits one NV12 texture. Returns false when the frame was dropped (encoder busy).</summary>
    public bool Encode(ID3D11Texture2D nv12, long sampleTime100ns, bool forceKeyframe)
    {
        if (Failed || _disposing)
        {
            return false;
        }

        if (_async && !_needInput.Wait(50))
        {
            DroppedInputs++;
            return false;
        }

        if (forceKeyframe)
        {
            var hr = _codecApi?.SetUInt32(CodecApi.AVEncVideoForceKeyFrame, 1) ?? -1;
            if (hr < 0 && !_forceKeyWarned)
            {
                _forceKeyWarned = true;
                Log.Warn($"CODECAPI_AVEncVideoForceKeyFrame not supported by {Name} ({HResults.Format(hr)})");
            }
        }

        using var sample = IsHardware ? CreateGpuSample(nv12) : CreateMemorySample(nv12);
        sample.SampleTime = sampleTime100ns;
        sample.SampleDuration = 10_000_000L / _settings.Fps;

        try
        {
            if (_async)
            {
                _mft.ProcessInput(0, sample, 0);
                return true;
            }

            try
            {
                _mft.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == HResults.MfENotAccepting)
            {
                while (ProcessOutputOnce())
                {
                }

                _mft.ProcessInput(0, sample, 0);
            }

            while (ProcessOutputOnce())
            {
            }

            return true;
        }
        catch (SharpGenException ex)
        {
            Failed = true;
            Log.Error($"Encoder {Name} ProcessInput failed: {HResults.Format(ex.ResultCode.Code)} {ex.Message.Trim()}");
            return false;
        }
    }

    /// <summary>SPS/PPS from MF_MT_MPEG_SEQUENCE_HEADER of the current output type, if the encoder exposes it.</summary>
    public byte[]? GetSequenceHeader()
    {
        try
        {
            using var type = _mft.GetOutputCurrentType(0);
            var size = type.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (size == 0)
            {
                return null;
            }

            var blob = type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            return AnnexB.ExtractParameterSets(blob);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private IMFSample CreateGpuSample(ID3D11Texture2D nv12)
    {
        var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12, 0, false);
        using (var buffer2D = buffer.QueryInterface<IMF2DBuffer>())
        {
            buffer.CurrentLength = buffer2D.ContiguousLength;
        }

        sample.AddBuffer(buffer);
        return sample;
    }

    private unsafe IMFSample CreateMemorySample(ID3D11Texture2D nv12)
    {
        int w = _settings.Width, h = _settings.Height;
        if (_staging is null)
        {
            var desc = nv12.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;
            _staging = _gpu.Device.CreateTexture2D(desc);
        }

        var ctx = _gpu.Context;
        ctx.CopyResource(_staging, nv12);
        var mapped = ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var size = w * h * 3 / 2;
        var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        try
        {
            buffer.Lock(out var dst, out _, out _);
            try
            {
                var src = (byte*)mapped.DataPointer;
                var d = (byte*)dst;
                var pitch = (int)mapped.RowPitch;
                for (var y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(src + (y * pitch), d + (y * w), w, w);
                }

                var srcUv = src + (pitch * h);
                var dstUv = d + (w * h);
                for (var y = 0; y < h / 2; y++)
                {
                    Buffer.MemoryCopy(srcUv + (y * pitch), dstUv + (y * w), w, w);
                }
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = size;
        }
        finally
        {
            ctx.Unmap(_staging, 0);
        }

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        return sample;
    }

    /// <summary>Pulls one output sample. Returns false when the MFT needs more input.</summary>
    private bool ProcessOutputOnce()
    {
        lock (_outputLock)
        {
            IMFSample? own = null;
            var odb = new OutputDataBuffer { StreamID = 0 };
            if (!_providesSamples)
            {
                own = MediaFactory.MFCreateSample();
                using var buffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                own.AddBuffer(buffer);
                odb.Sample = own;
            }

            try
            {
                var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
                if (hr.Code == HResults.MfETransformNeedMoreInput)
                {
                    return false;
                }

                if (hr.Code == HResults.MfETransformStreamChange)
                {
                    RenegotiateOutputType();
                    return true;
                }

                hr.CheckError();
                var sample = odb.Sample;
                if (sample is null)
                {
                    return true;
                }

                try
                {
                    var data = CopySample(sample);
                    if (data.Length > 0)
                    {
                        _onOutput(new EncodedSample(data, sample.SampleTime));
                    }
                }
                finally
                {
                    if (own is null || sample.NativePointer != own.NativePointer)
                    {
                        sample.Dispose();
                    }
                }

                return true;
            }
            finally
            {
                odb.Events?.Dispose();
                own?.Dispose();
            }
        }
    }

    private static byte[] CopySample(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var length);
        try
        {
            var data = new byte[length];
            Marshal.Copy(ptr, data, 0, length);
            return data;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private void RenegotiateOutputType()
    {
        using var type = _mft.GetOutputAvailableType(0, 0);
        _mft.SetOutputType(0, type, 0);
        Log.Debug("Encoder output type renegotiated (stream change).");
    }

    private void PumpEvents()
    {
        while (!_disposing)
        {
            IMFMediaEvent ev;
            try
            {
                ev = _events!.GetEvent(0);
            }
            catch (SharpGenException ex)
            {
                if (!_disposing)
                {
                    Failed = true;
                    Log.Error($"Encoder event queue failed: {HResults.Format(ex.ResultCode.Code)}");
                }

                break;
            }

            using (ev)
            {
                try
                {
                    switch (ev.EventType)
                    {
                        case MediaEventTypes.TransformNeedInput:
                            _needInput.Release();
                            break;
                        case MediaEventTypes.TransformHaveOutput:
                            ProcessOutputOnce();
                            break;
                        case MediaEventTypes.TransformDrainComplete:
                            break;
                        case MediaEventTypes.Error:
                            Failed = true;
                            Log.Error($"Encoder {Name} reported MEError {ev.Status}");
                            break;
                    }
                }
                catch (SharpGenException ex)
                {
                    if (!_disposing)
                    {
                        Failed = true;
                        Log.Error($"Encoder {Name} output failed: {HResults.Format(ex.ResultCode.Code)} {ex.Message.Trim()}");
                    }
                }
            }
        }
    }

    private void ApplyCodecSettings()
    {
        if (_codecApi is null)
        {
            Log.Warn($"{Name}: ICodecAPI not available; using defaults.");
            return;
        }

        void Set(string name, int hr)
        {
            if (hr < 0)
            {
                Log.Debug($"{Name}: {name} not accepted ({HResults.Format(hr)})");
            }
        }

        Set("AVLowLatencyMode", _codecApi.SetBool(CodecApi.AVLowLatencyMode, true));
        Set("AVEncCommonRateControlMode", _codecApi.SetUInt32(CodecApi.AVEncCommonRateControlMode, CodecApi.RateControlCbr));
        Set("AVEncCommonMeanBitRate", _codecApi.SetUInt32(CodecApi.AVEncCommonMeanBitRate, (uint)_settings.Bitrate));
        Set("AVEncMPVDefaultBPictureCount", _codecApi.SetUInt32(CodecApi.AVEncMPVDefaultBPictureCount, 0));
        Set("AVEncMPVGOPSize", _codecApi.SetUInt32(CodecApi.AVEncMPVGOPSize, (uint)_settings.GopFrames));
        Set("AVEncCommonQualityVsSpeed", _codecApi.SetUInt32(CodecApi.AVEncCommonQualityVsSpeed, 50));
    }

    private IMFMediaType CreateOutputType()
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_settings.Bitrate);
        SetCommonVideoAttributes(type);
        type.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)_settings.Profile);
        if (_settings.Level is { } level)
        {
            // eAVEncH264VLevel values are level_idc (e.g. 42 = 4.2).
            type.Set(MediaTypeAttributeKeys.Mpeg2Level, (uint)level);
        }
        return type;
    }

    private IMFMediaType CreateInputType()
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        SetCommonVideoAttributes(type);
        return type;
    }

    private void SetCommonVideoAttributes(IMFMediaType type)
    {
        MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)_settings.Width, (uint)_settings.Height).CheckError();
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)_settings.Fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);

        // BT.709 studio range, matching Nv12Converter.
        type.Set(MediaTypeAttributeKeys.YuvMatrix, 1u);          // MFVideoTransferMatrix_BT709
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);  // MFNominalRange_16_235
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u);     // MFVideoPrimaries_BT709
        type.Set(MediaTypeAttributeKeys.TransferFunction, 5u);   // MFVideoTransFunc_709
    }

    private static void TrySetAttribute(IMFAttributes attributes, Guid key, uint value)
    {
        try
        {
            attributes.Set(key, value);
        }
        catch (SharpGenException)
        {
        }
    }

    private static List<IMFActivate> EnumerateHardware(long adapterLuid)
    {
        // Restrict to the adapter that owns the captured output so textures can be shared.
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(MftEnumAdapterLuid, (ulong)adapterLuid);
        try
        {
            return Enumerate(MftEnumFlagHardware | MftEnumFlagSortAndFilter, attributes);
        }
        catch (SharpGenException)
        {
            return Enumerate(MftEnumFlagHardware | MftEnumFlagSortAndFilter, null);
        }
    }

    private static List<IMFActivate> Enumerate(uint flags, IMFAttributes? attributes)
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        IntPtr array;
        uint count;
        if (attributes is not null)
        {
            MediaFactory.MFTEnum2(TransformCategoryGuids.VideoEncoder, flags, input, output, attributes, out array, out count);
        }
        else
        {
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output, out array, out count);
        }

        var result = new List<IMFActivate>((int)count);
        if (array == IntPtr.Zero)
        {
            return result;
        }

        for (var i = 0; i < count; i++)
        {
            result.Add(new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size)));
        }

        Marshal.FreeCoTaskMem(array);
        return result;
    }

    private static string FriendlyName(IMFActivate activate)
    {
        try
        {
            return activate.GetAllocatedString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? "(unnamed MFT)";
        }
        catch (SharpGenException)
        {
            return "(unnamed MFT)";
        }
    }

    private static string VendorId(IMFActivate activate)
    {
        try
        {
            return activate.GetAllocatedString(TransformAttributeKeys.MftEnumHardwareVendorIdAttribute) ?? "vendor unknown";
        }
        catch (SharpGenException)
        {
            return "vendor unknown";
        }
    }

    public void Dispose()
    {
        if (_disposing)
        {
            return;
        }

        _disposing = true;
        try
        {
            _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }

        if (_async)
        {
            // Shutting the MFT down unblocks GetEvent in the pump thread.
            using (var shutdown = _mft.QueryInterfaceOrNull<IMFShutdown>())
            {
                try
                {
                    shutdown?.Shutdown();
                }
                catch (SharpGenException)
                {
                }
            }

            _pump?.Join(2000);
            _events?.Dispose();
        }

        _codecApi?.Dispose();
        _staging?.Dispose();
        _mft.Dispose();
        if (_activate is not null)
        {
            _activate.ShutdownObject();
            _activate.Dispose();
        }

        _needInput.Dispose();
    }
}
