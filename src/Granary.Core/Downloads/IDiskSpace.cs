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
