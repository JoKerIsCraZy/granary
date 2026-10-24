using System.Runtime.InteropServices;
using Granary.MpvSpike.Interop;
using Windows.Graphics.Imaging;

namespace Granary.MpvSpike;

/// <summary>Captures what DWM shows for a window (including DirectX content) into a PNG.</summary>
internal static class ScreenCapture
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

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

    public static async Task SaveWindowAsync(nint hwnd, string path)
    {
        var (pixels, width, height) = Grab(hwnd);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var file = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static unsafe (byte[] Pixels, int Width, int Height) Grab(nint hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        byte[] pixels = new byte[width * height * 4];

        nint screen = Native.GetDC(0);
        nint memory = Native.CreateCompatibleDC(screen);
        nint bitmap = Native.CreateCompatibleBitmap(screen, width, height);
        nint previous = Native.SelectObject(memory, bitmap);
        try
        {
            Native.BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, SrcCopy | CaptureBlt);
            var header = new BitmapInfoHeader
            {
                Size = (uint)sizeof(BitmapInfoHeader),
                Width = width,
                Height = -height, // top-down
                Planes = 1,
                BitCount = 32,
            };
            fixed (byte* bits = pixels)
            {
                Native.GetDIBits(memory, bitmap, 0, (uint)height, bits, &header, 0);
            }
        }
        finally
        {
            Native.SelectObject(memory, previous);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(0, screen);
        }
        return (pixels, width, height);
    }
}
