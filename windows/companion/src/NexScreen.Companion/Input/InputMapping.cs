namespace NexScreen.Companion.Input;

/// <summary>A rectangle in virtual-desktop pixels (physical pixels: the process is per-monitor DPI aware).</summary>
public readonly record struct DesktopRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"{Width}x{Height} at ({Left},{Top})";
}

/// <summary>Coordinate conversions for injected mouse input.</summary>
public static class InputMapping
{
    public const int MaxNormalized = 65535;

    /// <summary>
    /// NexLink normalized video coordinates (0..65535, 0,0 = top-left of the video) to a pixel of the captured
    /// output: 0 is its first pixel and 65535 its last, rounded to the nearest pixel. The video always shows the
    /// whole output (scaling keeps the aspect ratio), so no letterbox correction is needed here.
    /// </summary>
    public static (int X, int Y) NormalizedToDesktop(ushort x, ushort y, DesktopRect output)
    {
        if (output.IsEmpty)
        {
            throw new ArgumentException("Output rectangle is empty.", nameof(output));
        }

        return (output.Left + Scale(x, output.Width), output.Top + Scale(y, output.Height));

        static int Scale(ushort n, int size) => (int)(((long)n * (size - 1) + MaxNormalized / 2) / MaxNormalized);
    }

    /// <summary>
    /// A virtual-desktop pixel to SendInput absolute coordinates with MOUSEEVENTF_VIRTUALDESK, where 0..65535 spans
    /// the virtual screen (SM_XVIRTUALSCREEN/SM_CXVIRTUALSCREEN...). Windows converts back with
    /// pixel = origin + floor(n * size / 65536), so n = ceil((p - origin) * 65536 / size) lands exactly on p
    /// (for any size below 65536). Points outside the virtual screen are clamped to it.
    /// </summary>
    public static (int Dx, int Dy) DesktopToAbsolute(int x, int y, DesktopRect virtualScreen)
    {
        if (virtualScreen.IsEmpty)
        {
            throw new ArgumentException("Virtual screen is empty.", nameof(virtualScreen));
        }

        return (Normalize(x - virtualScreen.Left, virtualScreen.Width), Normalize(y - virtualScreen.Top, virtualScreen.Height));

        static int Normalize(int offset, int size)
        {
            var clamped = Math.Clamp(offset, 0, size - 1);
            return (int)Math.Min(MaxNormalized, ((long)clamped * 65536 + size - 1) / size);
        }
    }

    /// <summary>The pixel Windows moves the cursor to for absolute coordinates (inverse of <see cref="DesktopToAbsolute"/>).</summary>
    public static (int X, int Y) AbsoluteToDesktop(int dx, int dy, DesktopRect virtualScreen) =>
        (virtualScreen.Left + (int)((long)dx * virtualScreen.Width / 65536),
         virtualScreen.Top + (int)((long)dy * virtualScreen.Height / 65536));
}
