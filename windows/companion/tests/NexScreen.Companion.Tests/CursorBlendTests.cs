using NexScreen.Companion.Capture;

namespace NexScreen.Companion.Tests;

public class CursorBlendTests
{
    private static byte[] Fill(int w, int h, byte b, byte g, byte r)
    {
        var p = new byte[w * h * 4];
        for (var i = 0; i < p.Length; i += 4)
        {
            p[i] = b;
            p[i + 1] = g;
            p[i + 2] = r;
            p[i + 3] = 0;
        }

        return p;
    }

    private static (int B, int G, int R, int A) Px(byte[] p, int stride, int x, int y)
    {
        var i = y * stride + x * 4;
        return (p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    [Fact]
    public void Clip_Inside()
    {
        var r = CursorBlend.Clip(10, 20, 32, 32, 1920, 1080);
        Assert.Equal(new CursorRect(10, 20, 0, 0, 32, 32), r);
    }

    [Fact]
    public void Clip_TopLeftEdge()
    {
        var r = CursorBlend.Clip(-5, -3, 32, 32, 1920, 1080);
        Assert.Equal(new CursorRect(0, 0, 5, 3, 27, 29), r);
    }

    [Fact]
    public void Clip_BottomRightEdge()
    {
        var r = CursorBlend.Clip(1910, 1070, 32, 32, 1920, 1080);
        Assert.Equal(new CursorRect(1910, 1070, 0, 0, 10, 10), r);
    }

    [Theory]
    [InlineData(-32, 0)]
    [InlineData(1920, 0)]
    [InlineData(0, 1080)]
    [InlineData(0, -40)]
    public void Clip_Outside_IsEmpty(int x, int y) => Assert.True(CursorBlend.Clip(x, y, 32, 32, 1920, 1080).IsEmpty);

    [Fact]
    public void HotSpot_TopLeft()
    {
        var shape = new CursorShape(CursorShapeType.Color, 4, 4, 16, 2, 3, new byte[64]);
        Assert.Equal((98, 197), CursorBlend.TopLeftFromHotSpot(100, 200, shape));
    }

    [Fact]
    public void MonochromeShape_HalvesHeight()
    {
        var shape = new CursorShape(CursorShapeType.Monochrome, 8, 8, 1, 0, 0, new byte[8]);
        Assert.Equal(4, shape.Height);
    }

    [Fact]
    public void Shape_RejectsShortBuffer() =>
        Assert.Throws<ArgumentException>(() => new CursorShape(CursorShapeType.Color, 4, 4, 16, 0, 0, new byte[63]));

    [Fact]
    public void Color_AlphaBlend()
    {
        // 3x1 shape: opaque red, transparent, 50% blue.
        byte[] data = [0, 0, 255, 255, 9, 9, 9, 0, 255, 0, 0, 128];
        var shape = new CursorShape(CursorShapeType.Color, 3, 1, 12, 0, 0, data);
        var pixels = Fill(3, 1, 100, 100, 100);
        CursorBlend.Blend(shape, CursorBlend.Clip(0, 0, 3, 1, 3, 1), pixels, 12);
        Assert.Equal((0, 0, 255, 255), Px(pixels, 12, 0, 0));
        Assert.Equal((100, 100, 100, 255), Px(pixels, 12, 1, 0));
        Assert.Equal((178, 50, 50, 255), Px(pixels, 12, 2, 0)); // 255*128/255 + 100*127/255, rounded
    }

    [Fact]
    public void MaskedColor_ReplaceAndXor()
    {
        // 2x1: alpha 0 replaces with (1,2,3); alpha 0xFF XORs with (0xFF,0x0F,0x00).
        byte[] data = [1, 2, 3, 0, 0xFF, 0x0F, 0x00, 0xFF];
        var shape = new CursorShape(CursorShapeType.MaskedColor, 2, 1, 8, 0, 0, data);
        var pixels = Fill(2, 1, 0x12, 0x34, 0x56);
        CursorBlend.Blend(shape, CursorBlend.Clip(0, 0, 2, 1, 2, 1), pixels, 8);
        Assert.Equal((1, 2, 3, 255), Px(pixels, 8, 0, 0));
        Assert.Equal((0x12 ^ 0xFF, 0x34 ^ 0x0F, 0x56, 255), Px(pixels, 8, 1, 0));
    }

    [Fact]
    public void Monochrome_AllFourMaskCombinations()
    {
        // 4x1 visible (DXGI height 2), pitch 2 bytes. AND row: 1,1,0,0; XOR row: 0,1,0,1 (MSB first).
        byte[] data =
        [
            0b1100_0000, 0xAA, // AND (second byte is padding garbage)
            0b0101_0000, 0x55, // XOR
        ];
        var shape = new CursorShape(CursorShapeType.Monochrome, 4, 2, 2, 0, 0, data);
        var pixels = Fill(4, 1, 0x10, 0x80, 0xF0);
        CursorBlend.Blend(shape, CursorBlend.Clip(0, 0, 4, 1, 4, 1), pixels, 16);
        Assert.Equal((0x10, 0x80, 0xF0, 255), Px(pixels, 16, 0, 0)); // AND 1, XOR 0: transparent
        Assert.Equal((0xEF, 0x7F, 0x0F, 255), Px(pixels, 16, 1, 0)); // AND 1, XOR 1: invert
        Assert.Equal((0, 0, 0, 255), Px(pixels, 16, 2, 0)); // AND 0, XOR 0: black
        Assert.Equal((255, 255, 255, 255), Px(pixels, 16, 3, 0)); // AND 0, XOR 1: white
    }

    [Fact]
    public void Monochrome_BitsAcrossBytes()
    {
        // 10 wide: pixel 9 is bit 6 of byte 1.
        var data = new byte[2 * 2 * 1];
        data[0] = 0xFF;
        data[1] = 0xFF; // AND all set
        data[2 + 1] = 0b0100_0000; // XOR only pixel 9
        var shape = new CursorShape(CursorShapeType.Monochrome, 10, 2, 2, 0, 0, data);
        var pixels = Fill(10, 1, 0, 0, 0);
        CursorBlend.Blend(shape, CursorBlend.Clip(0, 0, 10, 1, 10, 1), pixels, 40);
        Assert.Equal((0, 0, 0, 255), Px(pixels, 40, 8, 0));
        Assert.Equal((255, 255, 255, 255), Px(pixels, 40, 9, 0));
    }

    [Fact]
    public void Blend_ClippedRegion_UsesShapeOffsetAndStride()
    {
        // 4x4 color shape, each pixel opaque with B = x, G = y. Drawn at (-2, -1) on a 3x3 frame.
        var data = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                data[i] = (byte)x;
                data[i + 1] = (byte)y;
                data[i + 2] = 200;
                data[i + 3] = 255;
            }
        }

        var shape = new CursorShape(CursorShapeType.Color, 4, 4, 16, 0, 0, data);
        var rect = CursorBlend.Clip(-2, -1, 4, 4, 3, 3);
        Assert.Equal(new CursorRect(0, 0, 2, 1, 2, 3), rect);

        const int stride = 12; // region buffer wider than the clipped width
        var pixels = new byte[stride * 3];
        CursorBlend.Blend(shape, rect, pixels, stride);
        Assert.Equal((2, 1, 200, 255), Px(pixels, stride, 0, 0));
        Assert.Equal((3, 3, 200, 255), Px(pixels, stride, 1, 2));
        Assert.Equal((0, 0, 0, 0), Px(pixels, stride, 2, 0)); // outside the rect: untouched
    }

    [Fact]
    public void Blend_RejectsRectOutsideShape()
    {
        var shape = new CursorShape(CursorShapeType.Color, 2, 2, 8, 0, 0, new byte[16]);
        Assert.Throws<ArgumentException>(() => CursorBlend.Blend(shape, new CursorRect(0, 0, 1, 0, 2, 2), new byte[16], 8));
    }

    [Fact]
    public void Options_NoCursor()
    {
        Assert.True(Options.Parse([]).DrawCursor);
        Assert.False(Options.Parse(["--no-cursor"]).DrawCursor);
    }
}
