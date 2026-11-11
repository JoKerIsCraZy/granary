using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>Free space where downloads go; the app puts in Windows' own count, which also measures network shares.</summary>
public interface IDiskSpace
{
    /// <summary>
    /// The bytes free to this user on the drive of <paramref name="folder"/>, or of its nearest parent that exists; null when
    /// that drive is not there, as one that is not plugged in.
    /// </summary>
    long? FreeBytes(string folder);
}

/// <summary>How the pages read the free space: off the UI thread, and never waiting on a drive or a share for long.</summary>
public static class DiskSpaceReads
{
    /// <summary>
    /// The longest a page waits for the free space: a share that does not answer would otherwise keep it waiting for half a
    /// minute. The transfer checks the space again before it writes.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <see cref="IDiskSpace.FreeBytes"/> on the thread pool; null (unknown) when the read fails or does not answer within
    /// <see cref="Timeout"/> on <paramref name="time"/>, which is logged without the system's message. A read that does not
    /// answer runs on by itself.
    /// </summary>
    public static async Task<long?> FreeBytesAsync(this IDiskSpace disk, string folder, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            return await Task.Run(() => disk.FreeBytes(folder)).WaitAsync(Timeout, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("The free space where downloads go did not come within {Seconds} s", Timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            // Whatever the drive does, the page stays usable: the space is then not known.
            logger.LogWarning("The free space where downloads go could not be read ({Failure})", DownloadLog.Failure(ex));
            return null;
        }
    }
}

/// <summary>Core's own <see cref="IDiskSpace"/>, through <see cref="DriveInfo"/>: drives with a letter only, null for anything else.</summary>
public sealed class DriveInfoDiskSpace : IDiskSpace
{
    public long? FreeBytes(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            string? existing = Path.GetFullPath(folder);
            while (existing is not null && !Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
            }
            if (existing is null || Path.GetPathRoot(existing) is not { Length: > 0 } root)
            {
                return null;
            }
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A drive DriveInfo cannot measure, such as a network share, counts as not there.
            return null;
        }
    }
}
