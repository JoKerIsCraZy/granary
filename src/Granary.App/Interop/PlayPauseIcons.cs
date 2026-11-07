using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>
/// The taskbar preview's play triangle and pause bars: white on clear, at Windows' small-icon size for a scale, so they
/// read on the preview's dark toolbar. Drawn here into 32-bit premultiplied BGRA (Windows' "Creating an Alpha Blended
/// Cursor"), the triangle's slanted edges smoothed by sampling each pixel 4 × 4 times; the bars' edges lie on whole pixels.
/// </summary>
internal sealed unsafe partial class PlayPauseIcons : IDisposable
{
    // SM_CXSMICON; 16 px at 100 %.
    private const int SmallIconWidthMetric = 49;
    private const int BaseSize = 16;
    private const uint BaseDpi = 96;
    private const int Samples = 4;

    private PlayPauseIcons(nint play, nint pause)
    {
        Play = play;
        Pause = pause;
    }

    /// <summary>The play triangle, for while a title is paused.</summary>
    public nint Play { get; private set; }

    /// <summary>The pause bars, for while a title plays.</summary>
    public nint Pause { get; private set; }

    /// <summary>Both icons at the small-icon size for the scale of the display the window is on.</summary>
    public static PlayPauseIcons ForWindow(nint hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        int size = GetSystemMetricsForDpi(SmallIconWidthMetric, dpi == 0 ? BaseDpi : dpi);
        if (size <= 0)
        {
            size = BaseSize;
        }
        // In 16 px: a shape 10 high, centred; bars 3 wide with 2 between them, the triangle 0.85 as wide as high.
        float scale = size / (float)BaseSize;
        int height = Math.Max(4, (int)MathF.Round(10 * scale));
        int top = (size - height) / 2;
        int bar = Math.Max(1, (int)MathF.Round(3 * scale));
        int gap = Math.Max(1, (int)MathF.Round(2 * scale));
        int barsLeft = (size - (2 * bar + gap)) / 2;
        float width = height * 0.85f;
        // Its box a little right of centre, halfway to where its weight (centroid) would be centred, so it looks centred;
        // the player nudges its glyph so too.
        float left = MathF.Round((size - width) / 2 + width / 12);
        float middle = top + height / 2f;

        nint play = Draw(size, (x, y) => y >= top && y <= top + height && x >= left
            && x <= left + width * (1 - Math.Abs(y - middle) / (height / 2f)));
        nint pause = 0;
        try
        {
            pause = Draw(size, (x, y) => y >= top && y < top + height
                && ((x >= barsLeft && x < barsLeft + bar) || (x >= barsLeft + bar + gap && x < barsLeft + 2 * bar + gap)));
        }
        finally
        {
            if (pause == 0)
            {
                DestroyIcon(play);
            }
        }
        return new PlayPauseIcons(play, pause);
    }

    public void Dispose()
    {
        if (Play != 0)
        {
            DestroyIcon(Play);
            Play = 0;
        }
        if (Pause != 0)
        {
            DestroyIcon(Pause);
            Pause = 0;
        }
    }

    // White where the shape covers a pixel, as much as it covers it.
    private static nint Draw(int size, Func<float, float, bool> inside)
    {
        var pixels = new uint[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int covered = 0;
                for (int sy = 0; sy < Samples; sy++)
                {
                    for (int sx = 0; sx < Samples; sx++)
                    {
                        if (inside(x + (sx + 0.5f) / Samples, y + (sy + 0.5f) / Samples))
                        {
                            covered++;
                        }
                    }
                }
                uint alpha = (uint)(covered * 255 / (Samples * Samples));
                // Premultiplied white: blue, green and red equal the alpha.
                pixels[y * size + x] = alpha << 24 | alpha << 16 | alpha << 8 | alpha;
            }
        }
        return ToIcon(size, pixels);
    }

    private static nint ToIcon(int size, uint[] pixels)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = size,
            // Negative: top-down rows.
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };
        // The AND mask, one bit a pixel in rows of whole 16-bit words: set (transparent) where nothing is drawn.
        int stride = (size + 15) / 16 * 2;
        var mask = new byte[stride * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (pixels[y * size + x] == 0)
                {
                    mask[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
        }
        nint color = CreateDIBSection(0, &header, 0, out void* bits, 0, 0);
        if (color == 0)
        {
            throw new InvalidOperationException("The icon's picture could not be made.");
        }
        nint maskBitmap = 0;
        try
        {
            if (bits is null)
            {
                throw new InvalidOperationException("The icon's picture has no pixels.");
            }
            pixels.AsSpan().CopyTo(new Span<uint>(bits, pixels.Length));
            fixed (byte* maskBits = mask)
            {
                maskBitmap = CreateBitmap(size, size, 1, 1, maskBits);
            }
            if (maskBitmap == 0)
            {
                throw new InvalidOperationException("The icon's mask could not be made.");
            }
            var info = new IconInfo { IsIcon = 1, Mask = maskBitmap, Color = color };
            nint icon = CreateIconIndirect(&info);
            return icon != 0 ? icon : throw new InvalidOperationException("The icon could not be made.");
        }
        finally
        {
            // The icon has its own copies.
            DeleteObject(color);
            if (maskBitmap != 0)
            {
                DeleteObject(maskBitmap);
            }
        }
    }

    // BITMAPINFOHEADER; BI_RGB, no colour table.
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    // ICONINFO.
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, BitmapInfoHeader* header, uint usage, out void* bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte* bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(IconInfo* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
