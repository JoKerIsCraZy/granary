using System.Diagnostics;

namespace Granary.MpvSpike;

/// <summary>Thread-safe line logger with elapsed milliseconds; never logs header values.</summary>
internal static class SpikeLog
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter? _writer;

    public static void Open(string path)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        }
    }

    public static void Write(string category, string message)
    {
        string line = $"{Clock.ElapsedMilliseconds,8} [{category}] {message}";
        lock (Gate)
        {
            _writer?.WriteLine(line);
        }
        Debug.WriteLine(line);
    }

    public static void Close()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
