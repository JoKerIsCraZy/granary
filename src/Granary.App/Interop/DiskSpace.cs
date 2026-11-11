using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>Windows' own count of free space (<c>GetDiskFreeSpaceExW</c>): on a drive with a letter, and on a network share.</summary>
internal static partial class DiskSpace
{
    // Windows' file names stop here unless the path is written in its extended form (\\?\) or the process is long-path aware
    // with the system's LongPathsEnabled on (app.manifest).
    private const int MaxPath = 260;

    /// <summary>
    /// The bytes free to this user on the volume of <paramref name="directory"/>, a full path of a folder that exists; null when
    /// Windows can't tell, with the system's error code.
    /// </summary>
    public static long? FreeBytes(string directory, out int error)
    {
        error = 0;
        if (GetDiskFreeSpaceExW(Extended(WithSeparator(directory)), out ulong available, out _, out _))
        {
            return available > long.MaxValue ? long.MaxValue : (long)available;
        }
        error = Marshal.GetLastPInvokeError();
        return null;
    }

    // A share's root is asked for with its trailing backslash (\\server\share\), as the call needs it.
    private static string WithSeparator(string directory) =>
        Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;

    // A download's folder can be deeper than Windows' usual 260 characters: such a path goes in the extended form, which
    // Windows takes whatever the system's long path setting is.
    private static string Extended(string path)
    {
        if (path.Length < MaxPath || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
