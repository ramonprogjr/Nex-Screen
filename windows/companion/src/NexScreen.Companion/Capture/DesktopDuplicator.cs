using NexScreen.Companion.Util;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NexScreen.Companion.Capture;

public enum AcquireResult
{
    NewFrame,
    NoChange,
    Timeout,
    AccessLost,
}

/// <summary>
/// DXGI Desktop Duplication of one output. Each new desktop image is copied (GPU to GPU) into a texture
/// owned by this object, so the duplication frame can be released immediately. With <see cref="DrawCursor"/>
/// the pointer (which duplication keeps out of the image) is drawn into a second copy, and pointer moves
/// or shape changes count as new frames.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly IDXGIOutput1 _output;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _frame;
    private ID3D11Texture2D? _composed;
    private CursorOverlay? _overlay;
    private CursorShape? _shape;
    private byte[] _shapeBuffer = [];
    private bool _pointerVisible;
    private int _pointerX;
    private int _pointerY;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ModeRotation Rotation { get; private set; }

    /// <summary>
    /// Latest image to encode (BGRA), owned by this object: the desktop, with the pointer drawn in when
    /// <see cref="DrawCursor"/> is set. Valid after <see cref="Acquire"/> returned <see cref="AcquireResult.NewFrame"/>.
    /// </summary>
    public ID3D11Texture2D? Frame => _composed ?? _frame;

    /// <summary>Draw the pointer into <see cref="Frame"/> (default true). Skipped on rotated outputs.</summary>
    public bool DrawCursor { get; set; } = true;

    /// <summary>Number of pointer-only updates (move, show/hide, shape) reported as new frames.</summary>
    public long PointerFrames { get; private set; }

    private bool CursorActive => DrawCursor && Rotation is ModeRotation.Identity or ModeRotation.Unspecified;

    /// <summary>Monotonic capture time of <see cref="Frame"/> in microseconds.</summary>
    public ulong FrameTimestampUs { get; private set; }

    public DesktopDuplicator(GpuDevice gpu, int outputIndex)
    {
        _gpu = gpu;
        gpu.Adapter.EnumOutputs((uint)outputIndex, out var output).CheckError();
        using (output)
        {
            _output = output.QueryInterface<IDXGIOutput1>();
        }
    }

    /// <summary>(Re)creates the duplication. Returns false when it is temporarily unavailable (secure desktop, mode change).</summary>
    public bool TryStart(out string? error)
    {
        ReleaseDuplication();
        NativeMethods.TryAttachToInputDesktop();
        try
        {
            using var output5 = _output.QueryInterfaceOrNull<IDXGIOutput5>();
            if (output5 is not null)
            {
                _duplication = output5.DuplicateOutput1(_gpu.Device, [Format.B8G8R8A8_UNorm]);
            }
            else
            {
                _duplication = _output.DuplicateOutput(_gpu.Device);
            }
        }
        catch (SharpGenException ex)
        {
            error = $"{HResults.Format(ex.ResultCode.Code)} {ex.Message.Trim()}";
            return false;
        }

        var desc = _duplication.Description;
        Rotation = desc.Rotation;
        var w = (int)desc.ModeDescription.Width;
        var h = (int)desc.ModeDescription.Height;
        if (_frame is null || w != Width || h != Height)
        {
            _frame?.Dispose();
            _frame = null;
            Width = w;
            Height = h;
        }

        error = null;
        return true;
    }

    public AcquireResult Acquire(int timeoutMs)
    {
        if (_duplication is null)
        {
            return AcquireResult.AccessLost;
        }

        var hr = _duplication.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out var resource);
        if (hr.Code == HResults.DxgiErrorWaitTimeout)
        {
            return AcquireResult.Timeout;
        }

        if (hr.Code is HResults.DxgiErrorAccessLost or HResults.DxgiErrorInvalidCall or HResults.DxgiErrorSessionDisconnected)
        {
            ReleaseDuplication();
            return AcquireResult.AccessLost;
        }

        hr.CheckError();
        try
        {
            var pointerChanged = CursorActive && UpdatePointer(info);

            // LastPresentTime == 0 means only the pointer changed.
            var desktopChanged = info.LastPresentTime != 0 && resource is not null;
            if (desktopChanged)
            {
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                EnsureFrameTexture(texture.Description);
                _gpu.Context.CopyResource(_frame!, texture);
            }
            else if (!pointerChanged || _frame is null)
            {
                return AcquireResult.NoChange;
            }
            else
            {
                PointerFrames++;
            }

            ComposeFrame();
            FrameTimestampUs = MonoClock.NowMicroseconds;
            return AcquireResult.NewFrame;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    /// <summary>Applies the frame's pointer position / shape updates. Returns true when the drawn pointer changed.</summary>
    private bool UpdatePointer(OutduplFrameInfo info)
    {
        var changed = false;
        if (info.LastMouseUpdateTime != 0)
        {
            var p = info.PointerPosition;
            bool visible = p.Visible;
            int x = p.Position.X, y = p.Position.Y;
            if ((visible || _pointerVisible) && (visible != _pointerVisible || x != _pointerX || y != _pointerY))
            {
                changed = true;
            }

            _pointerVisible = visible;
            _pointerX = x;
            _pointerY = y;
        }

        if (info.PointerShapeBufferSize > 0 && TryReadPointerShape(info.PointerShapeBufferSize))
        {
            changed |= _pointerVisible;
        }

        return changed;
    }

    private unsafe bool TryReadPointerShape(uint size)
    {
        if (_shapeBuffer.Length < size)
        {
            _shapeBuffer = new byte[size];
        }

        Result hr;
        uint required;
        OutduplPointerShapeInfo shapeInfo;
        fixed (byte* buffer = _shapeBuffer)
        {
            hr = _duplication!.GetFramePointerShape(size, (IntPtr)buffer, out required, out shapeInfo);
        }

        if (hr.Failure)
        {
            Log.Debug($"GetFramePointerShape failed: {HResults.Format(hr.Code)}");
            return false;
        }

        try
        {
            var type = (CursorShapeType)(uint)shapeInfo.Type;
            _shape = new CursorShape(type, (int)shapeInfo.Width, (int)shapeInfo.Height, (int)shapeInfo.Pitch,
                shapeInfo.HotSpot.X, shapeInfo.HotSpot.Y, _shapeBuffer.AsSpan(0, (int)Math.Min(size, required)).ToArray());
            Log.Debug($"Pointer shape: {type} {_shape.Width}x{_shape.Height}, hot spot {_shape.HotSpotX},{_shape.HotSpotY}");
            return true;
        }
        catch (ArgumentException ex)
        {
            Log.Debug($"Ignoring pointer shape: {ex.Message}");
            return false;
        }
    }

    private void ComposeFrame()
    {
        if (!CursorActive || _frame is null)
        {
            _composed = null;
            return;
        }

        // DXGI's pointer position is already the shape's top-left (hot spot accounted for).
        _overlay ??= new CursorOverlay(_gpu);
        _composed = _overlay.Compose(_frame, _shape, _pointerVisible, _pointerX, _pointerY);
    }

    private void EnsureFrameTexture(Texture2DDescription source)
    {
        if (_frame is not null)
        {
            var d = _frame.Description;
            if (d.Width == source.Width && d.Height == source.Height && d.Format == source.Format)
            {
                return;
            }

            _frame.Dispose();
        }

        _frame = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });
        Width = (int)source.Width;
        Height = (int)source.Height;
    }

    private void ReleaseDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    public void Dispose()
    {
        ReleaseDuplication();
        _overlay?.Dispose();
        _frame?.Dispose();
        _output.Dispose();
    }
}
