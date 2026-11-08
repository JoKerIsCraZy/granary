using System.Text;

namespace Granary.Tests.Fakes;

/// <summary>Writes a small silent YUV4MPEG2 video of grey frames, so player tests with a picture need no media files.</summary>
internal static class Y4mFile
{
    private const int Width = 16;
    private const int Height = 16;
    private const int FramesPerSecond = 25;

    public static string WriteTemp(double seconds)
    {
        string path = Path.Combine(Path.GetTempPath(), "granary-tests", $"{Guid.NewGuid():N}.y4m");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{Width} H{Height} F{FramesPerSecond}:1 Ip A1:1 C420jpeg\n"));
        // 4:2:0: a full-size luma plane, then two quarter-size chroma planes; 128 everywhere is mid grey.
        var frame = new byte[(Width * Height) + (2 * (Width / 2) * (Height / 2))];
        Array.Fill(frame, (byte)128);
        int frames = (int)(seconds * FramesPerSecond);
        for (int i = 0; i < frames; i++)
        {
            stream.Write("FRAME\n"u8);
            stream.Write(frame);
        }
        return path;
    }
}
