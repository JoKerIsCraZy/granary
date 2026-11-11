using Granary.App.Interop;
using Granary.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// The free space where downloads go, as Windows counts it (<c>GetDiskFreeSpaceExW</c>): it also measures a network share,
/// which Core's <see cref="DriveInfoDiskSpace"/> cannot. A folder not made yet is measured on its nearest parent that exists;
/// null when none does, as on a drive that is not plugged in, and when Windows cannot tell, which is logged with its code.
/// </summary>
public sealed class WindowsDiskSpace(ILogger<WindowsDiskSpace> logger) : IDiskSpace
{
    public long? FreeBytes(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string? existing;
        try
        {
            existing = Path.GetFullPath(folder);
            while (existing is not null && !Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A path Windows cannot take counts as not there. Its message would name the folder, with the Windows user name.
            logger.LogWarning("The folder downloads go to could not be looked at ({Failure})", ex.GetType().Name);
            return null;
        }
        if (existing is null)
        {
            return null;
        }
        long? free = DiskSpace.FreeBytes(existing, out int error);
        if (free is null)
        {
            logger.LogWarning("Windows did not say the free space where downloads go (error {Error})", error);
        }
        return free;
    }
}
