namespace Granary.Tests.Fakes;

/// <summary>Writes a small 16-bit mono PCM WAV with a sine tone, so player tests need no media files.</summary>
internal static class WavFile
{
    public static byte[] Sine(double seconds, int sampleRate = 8000, double frequency = 440)
    {
        int samples = (int)(seconds * sampleRate);
        int dataBytes = samples * 2;
        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (int i = 0; i < samples; i++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.2));
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static string WriteTemp(double seconds)
    {
        string path = Path.Combine(Path.GetTempPath(), "granary-tests", $"{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Sine(seconds));
        return path;
    }
}
