namespace Granary.Core.Abstractions;

/// <summary>Opens one of Granary's files or folders on the desktop, for the viewer to work with outside Granary.</summary>
public interface IFileLauncher
{
    /// <summary>Opens the file in an editor; false when nothing could open it.</summary>
    bool OpenFile(string path);

    /// <summary>Shows the folder in File Explorer; false when it could not.</summary>
    bool OpenFolder(string path);
}
