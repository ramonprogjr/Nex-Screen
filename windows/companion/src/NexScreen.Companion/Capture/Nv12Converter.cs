using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;
using Vortice.Mathematics;

namespace NexScreen.Companion.Capture;

/// <summary>
/// BGRA to NV12 (BT.709, limited range) conversion and scaling on the GPU with ID3D11VideoProcessor.
/// Output goes to a small ring of NV12 textures so the encoder can still be reading older ones.
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    private const int RingSize = 8;

    private readonly GpuDevice _gpu;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D[] _ring = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[RingSize];
    private ID3D11VideoProcessorInputView? _inputView;
    private IntPtr _inputViewTexture;
    private int _next;

    public int InputWidth { get; }
    public int InputHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }

    /// <summary>Most recently converted texture (kept for keep-alive and keyframe re-encodes).</summary>
    public ID3D11Texture2D? Last { get; private set; }

    public Nv12Converter(GpuDevice gpu, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        _gpu = gpu;
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;

        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // Desktop is full-range RGB; the encoder input is BT.709 studio-range YCbCr.
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace
        {
            Usage = 0,
            RGB_Range = 0,
            YCbCr_Matrix = 1,
            YCbCr_xvYCC = 0,
            Nominal_Range = 2,
        });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
        {
            Usage = 0,
            RGB_Range = 0,
            YCbCr_Matrix = 1,
            YCbCr_xvYCC = 0,
            Nominal_Range = 1,
        });
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);

        // Letterbox when the aspect ratio differs; black background (Y=16 in studio range via RGB black).
        var dest = FitRect(inputWidth, inputHeight, outputWidth, outputHeight);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new RawRect(0, 0, inputWidth, inputHeight));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, dest);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, outputWidth, outputHeight));
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });

        for (var i = 0; i < RingSize; i++)
        {
            _ring[i] = CreateNv12Texture(gpu.Device, outputWidth, outputHeight);
            _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_ring[i], _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
        }
    }

    public ID3D11Texture2D Convert(ID3D11Texture2D bgra)
    {
        if (_inputView is null || _inputViewTexture != bgra.NativePointer)
        {
            _inputView?.Dispose();
            _inputView = _videoDevice.CreateVideoProcessorInputView(bgra, _enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
            _inputViewTexture = bgra.NativePointer;
        }

        var index = _next;
        _next = (_next + 1) % RingSize;
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
        _videoContext.VideoProcessorBlt(_processor, _outputViews[index], 0, 1, [stream]).CheckError();
        Last = _ring[index];
        return Last;
    }

    internal static RawRect FitRect(int srcW, int srcH, int dstW, int dstH)
    {
        // Integer math keeps even offsets so chroma lines up.
        long scaledW = dstW, scaledH = (long)srcH * dstW / srcW;
        if (scaledH > dstH)
        {
            scaledH = dstH;
            scaledW = (long)srcW * dstH / srcH;
        }

        var x = (int)((dstW - scaledW) / 2) & ~1;
        var y = (int)((dstH - scaledH) / 2) & ~1;
        return new RawRect(x, y, x + (int)scaledW, y + (int)scaledH);
    }

    private static ID3D11Texture2D CreateNv12Texture(ID3D11Device device, int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        try
        {
            return device.CreateTexture2D(desc);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            desc.BindFlags = BindFlags.RenderTarget;
            return device.CreateTexture2D(desc);
        }
    }

    public void Dispose()
    {
        _inputView?.Dispose();
        foreach (var v in _outputViews)
        {
            v?.Dispose();
        }

        foreach (var t in _ring)
        {
            t?.Dispose();
        }

        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
