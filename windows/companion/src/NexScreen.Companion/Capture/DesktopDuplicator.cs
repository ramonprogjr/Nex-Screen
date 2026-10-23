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
/// DXGI Desktop Duplication of one output. Each new desktop image is copied (GPU to GPU) into
/// <see cref="Frame"/>, so the duplication frame can be released immediately.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly IDXGIOutput1 _output;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _frame;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public ModeRotation Rotation { get; private set; }

    /// <summary>Latest captured desktop image (BGRA), owned by this object.</summary>
    public ID3D11Texture2D? Frame => _frame;

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
            // LastPresentTime == 0 means only the pointer changed.
            if (info.LastPresentTime == 0 || resource is null)
            {
                return AcquireResult.NoChange;
            }

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            EnsureFrameTexture(texture.Description);
            _gpu.Context.CopyResource(_frame!, texture);
            FrameTimestampUs = MonoClock.NowMicroseconds;
            return AcquireResult.NewFrame;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
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
        _frame?.Dispose();
        _output.Dispose();
    }
}
