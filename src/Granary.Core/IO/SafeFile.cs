namespace Granary.Core.IO;

/// <summary>
/// Replaces a file whole: written beside it first, then moved over it, so a write that fails never leaves half a file.
/// Granary's own JSON files and mpv.conf are written this way.
/// </summary>
internal static class SafeFile
{
    // Enough for a read of a small file to end; about 200 ms in all. Writes run off the UI thread.
    private const int MoveTries = 6;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Has <paramref name="writeTemp"/> write the new content to a temporary file beside <paramref name="path"/>, then moves it
    /// over the file. <paramref name="beforeRetry"/> runs before a move that something holding the file open got in the way
    /// of tries again (for tests).
    /// </summary>
    /// <exception cref="IOException">It could not be written; the temporary file is gone and the file is as it was.</exception>
    /// <exception cref="UnauthorizedAccessException">As <see cref="IOException"/>.</exception>
    public static void WriteWhole(string path, Action<string> writeTemp, Action? beforeRetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeTemp);
        // Its own temporary name, in the same folder so the move replaces the file at once: a leftover of an earlier write, or
        // a second Granary on this data folder writing now, cannot get in its way.
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            writeTemp(temp);
            FlushToDisk(temp);
            MoveOver(temp, path, beforeRetry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw;
        }
    }

    // The new content is on the disk before it replaces the file: a power cut after the move leaves the new file whole, not
    // one of the right length full of zeros.
    private static void FlushToDisk(string temp)
    {
        using var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    // Windows refuses to replace a file while anything has it open, whatever that shares: a read Granary started by itself
    // (the settings row, the player's check) or a virus scanner's. Those end in a moment; a file held for good still fails.
    // So does one whose last write or size changed while it waited: that is another program's save, and it stays.
    private static void MoveOver(string temp, string path, Action? beforeRetry)
    {
        var before = Stamp(path);
        for (int tries = 1; ; tries++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && tries < MoveTries)
            {
                beforeRetry?.Invoke();
                Thread.Sleep(MoveRetryDelay);
            }
            if (Stamp(path) != before)
            {
                throw new IOException("The file changed while the write waited for it.");
            }
        }
    }

    // A file's last write and size, which a read leaves alone and a save does not. Readable while another program has it
    // open, though Windows may only show a change once that program lets go.
    private static (DateTime LastWrite, long Length) Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
    }

    private static void TryDelete(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It stays behind under its own name, where it gets in no write's way; the caller logs the write that failed.
        }
    }
}
