using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NexScreen.Companion.Capture;

/// <summary>
/// Draws the pointer into a copy of the captured desktop. The full frame is copied on the GPU; only the
/// pointer rectangle (at most a few hundred pixels square) goes through a small staging texture, is
/// blended on the CPU (needed for the XOR semantics of monochrome and masked-color shapes) and written back.
/// </summary>
public sealed class CursorOverlay : IDisposable
{
    private readonly GpuDevice _gpu;
    private ID3D11Texture2D? _composite;
    private ID3D11Texture2D? _staging;
    private byte[] _pixels = [];

    public CursorOverlay(GpuDevice gpu) => _gpu = gpu;

    /// <summary>
    /// Copies <paramref name="desktop"/> into the overlay's own texture and draws the pointer there.
    /// <paramref name="desktop"/> itself is never modified.
    /// </summary>
    public ID3D11Texture2D Compose(ID3D11Texture2D desktop, CursorShape? shape, bool visible, int left, int top)
    {
        var desc = desktop.Description;
        EnsureComposite(desc);
        var context = _gpu.Context;
        context.CopyResource(_composite!, desktop);

        if (!visible || shape is null || desc.Format is not (Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb))
        {
            return _composite!;
        }

        var rect = CursorBlend.Clip(left, top, shape.Width, shape.Height, (int)desc.Width, (int)desc.Height);
        if (rect.IsEmpty)
        {
            return _composite!;
        }

        EnsureStaging(rect.Width, rect.Height, desc.Format);
        var box = new Box(rect.DstX, rect.DstY, 0, rect.DstX + rect.Width, rect.DstY + rect.Height, 1);
        context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, desktop, 0, box);

        var stride = rect.Width * 4;
        if (_pixels.Length < stride * rect.Height)
        {
            _pixels = new byte[stride * rect.Height];
        }

        context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
        try
        {
            for (var y = 0; y < rect.Height; y++)
            {
                Marshal.Copy(mapped.DataPointer + (nint)(y * (long)mapped.RowPitch), _pixels, y * stride, stride);
            }
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }

        CursorBlend.Blend(shape, rect, _pixels, stride);

        var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            context.UpdateSubresource(_composite!, 0, box, handle.AddrOfPinnedObject(), (uint)stride, 0);
        }
        finally
        {
            handle.Free();
        }

        return _composite!;
    }

    private void EnsureComposite(Texture2DDescription source)
    {
        if (_composite is not null)
        {
            var d = _composite.Description;
            if (d.Width == source.Width && d.Height == source.Height && d.Format == source.Format)
            {
                return;
            }

            _composite.Dispose();
        }

        _composite = _gpu.Device.CreateTexture2D(new Texture2DDescription
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
    }

    private void EnsureStaging(int width, int height, Format format)
    {
        if (_staging is not null)
        {
            var d = _staging.Description;
            if (d.Width >= width && d.Height >= height && d.Format == format)
            {
                return;
            }

            width = Math.Max(width, (int)d.Width);
            height = Math.Max(height, (int)d.Height);
            _staging.Dispose();
        }

        _staging = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    public void Dispose()
    {
        _composite?.Dispose();
        _staging?.Dispose();
    }
}
