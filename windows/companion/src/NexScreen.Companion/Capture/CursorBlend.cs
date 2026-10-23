namespace NexScreen.Companion.Capture;

/// <summary>DXGI_OUTDUPL_POINTER_SHAPE_TYPE values.</summary>
public enum CursorShapeType : uint
{
    Monochrome = 1,
    Color = 2,
    MaskedColor = 4,
}

/// <summary>
/// A pointer shape as returned by IDXGIOutputDuplication::GetFramePointerShape.
/// <see cref="Height"/> is the visible height: for monochrome shapes DXGI reports twice that
/// (AND mask rows followed by XOR mask rows).
/// </summary>
public sealed class CursorShape
{
    public CursorShapeType Type { get; }
    public int Width { get; }
    public int Height { get; }
    public int Pitch { get; }
    public int HotSpotX { get; }
    public int HotSpotY { get; }
    public byte[] Data { get; }

    public CursorShape(CursorShapeType type, int width, int dxgiHeight, int pitch, int hotSpotX, int hotSpotY, byte[] data)
    {
        if (type is not (CursorShapeType.Monochrome or CursorShapeType.Color or CursorShapeType.MaskedColor))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown pointer shape type.");
        }

        var height = type == CursorShapeType.Monochrome ? dxgiHeight / 2 : dxgiHeight;
        var minPitch = type == CursorShapeType.Monochrome ? (width + 7) / 8 : width * 4;
        var rows = type == CursorShapeType.Monochrome ? height * 2 : height;
        if (width <= 0 || height <= 0 || pitch < minPitch || (long)pitch * rows > data.Length)
        {
            throw new ArgumentException($"Inconsistent pointer shape {type} {width}x{dxgiHeight} pitch {pitch}, {data.Length} bytes.");
        }

        Type = type;
        Width = width;
        Height = height;
        Pitch = pitch;
        HotSpotX = hotSpotX;
        HotSpotY = hotSpotY;
        Data = data;
    }
}

/// <summary>Where a pointer shape lands on the frame after clipping.</summary>
/// <param name="DstX">Left of the visible part on the frame.</param>
/// <param name="DstY">Top of the visible part on the frame.</param>
/// <param name="SrcX">Matching column inside the shape.</param>
/// <param name="SrcY">Matching row inside the shape.</param>
public readonly record struct CursorRect(int DstX, int DstY, int SrcX, int SrcY, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>CPU pointer compositing on small BGRA regions (pure, unit-tested).</summary>
public static class CursorBlend
{
    /// <summary>
    /// Clips a shape drawn with its top-left at (left, top) against a frame. DXGI's
    /// PointerPosition.Position is already the shape's top-left (the hot spot is subtracted by the
    /// system), so callers pass it as is; see <see cref="TopLeftFromHotSpot"/> for a hot-spot position.
    /// </summary>
    public static CursorRect Clip(int left, int top, int shapeWidth, int shapeHeight, int frameWidth, int frameHeight)
    {
        var x0 = Math.Max(left, 0);
        var y0 = Math.Max(top, 0);
        var x1 = Math.Min((long)left + shapeWidth, frameWidth);
        var y1 = Math.Min((long)top + shapeHeight, frameHeight);
        if (x1 <= x0 || y1 <= y0)
        {
            return default;
        }

        return new CursorRect(x0, y0, x0 - left, y0 - top, (int)(x1 - x0), (int)(y1 - y0));
    }

    /// <summary>Top-left of the shape when the pointer's hot spot is at (x, y).</summary>
    public static (int Left, int Top) TopLeftFromHotSpot(int x, int y, CursorShape shape) =>
        (x - shape.HotSpotX, y - shape.HotSpotY);

    /// <summary>
    /// Blends the clipped part of <paramref name="shape"/> into <paramref name="pixels"/>, a BGRA
    /// region of rect.Width x rect.Height holding the desktop pixels under the pointer. Output alpha is 0xFF.
    /// </summary>
    public static void Blend(CursorShape shape, CursorRect rect, Span<byte> pixels, int stride)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        if (stride < rect.Width * 4 || (long)stride * (rect.Height - 1) + rect.Width * 4 > pixels.Length ||
            rect.SrcX < 0 || rect.SrcY < 0 || rect.SrcX + rect.Width > shape.Width || rect.SrcY + rect.Height > shape.Height)
        {
            throw new ArgumentException("Pointer blend rectangle does not fit the shape or the pixel buffer.");
        }

        var src = shape.Data;
        for (var row = 0; row < rect.Height; row++)
        {
            var sy = rect.SrcY + row;
            var dst = pixels.Slice(row * stride, rect.Width * 4);
            for (var col = 0; col < rect.Width; col++)
            {
                var sx = rect.SrcX + col;
                var d = dst.Slice(col * 4, 4);
                switch (shape.Type)
                {
                    case CursorShapeType.Color:
                    {
                        // Straight (non-premultiplied) alpha, like the DXGI sample's SRC_ALPHA/INV_SRC_ALPHA blend.
                        var s = sy * shape.Pitch + sx * 4;
                        int a = src[s + 3];
                        if (a == 255)
                        {
                            d[0] = src[s];
                            d[1] = src[s + 1];
                            d[2] = src[s + 2];
                        }
                        else if (a != 0)
                        {
                            var ia = 255 - a;
                            d[0] = (byte)((src[s] * a + d[0] * ia + 127) / 255);
                            d[1] = (byte)((src[s + 1] * a + d[1] * ia + 127) / 255);
                            d[2] = (byte)((src[s + 2] * a + d[2] * ia + 127) / 255);
                        }

                        break;
                    }

                    case CursorShapeType.MaskedColor:
                    {
                        // Alpha 0xFF: XOR the RGB with the screen; alpha 0x00: replace the screen pixel.
                        var s = sy * shape.Pitch + sx * 4;
                        if (src[s + 3] != 0)
                        {
                            d[0] ^= src[s];
                            d[1] ^= src[s + 1];
                            d[2] ^= src[s + 2];
                        }
                        else
                        {
                            d[0] = src[s];
                            d[1] = src[s + 1];
                            d[2] = src[s + 2];
                        }

                        break;
                    }

                    default:
                    {
                        // Monochrome: screen = (screen AND and_bit) XOR xor_bit, per 1bpp mask (MSB first).
                        var bit = (byte)(0x80 >> (sx & 7));
                        var andSet = (src[sy * shape.Pitch + (sx >> 3)] & bit) != 0;
                        var xorSet = (src[(sy + shape.Height) * shape.Pitch + (sx >> 3)] & bit) != 0;
                        var and = andSet ? (byte)0xFF : (byte)0x00;
                        var xor = xorSet ? (byte)0xFF : (byte)0x00;
                        d[0] = (byte)((d[0] & and) ^ xor);
                        d[1] = (byte)((d[1] & and) ^ xor);
                        d[2] = (byte)((d[2] & and) ^ xor);
                        break;
                    }
                }

                d[3] = 0xFF;
            }
        }
    }
}
