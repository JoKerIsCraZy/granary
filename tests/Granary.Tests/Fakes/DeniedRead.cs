using System.Security.AccessControl;
using System.Security.Principal;

namespace Granary.Tests.Fakes;

/// <summary>
/// A file this user may neither read nor look at, nor list in its folder, until disposed. File.Exists answers false for such a
/// file, although it is there.
/// </summary>
internal sealed class DeniedRead : IDisposable
{
    private readonly FileInfo _file;
    private readonly DirectoryInfo _folder;
    private readonly FileSystemAccessRule _fileRule;
    private readonly FileSystemAccessRule _folderRule;

    public DeniedRead(string file)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        _file = new FileInfo(file);
        _folder = _file.Directory!;
        _fileRule = new FileSystemAccessRule(user, FileSystemRights.ReadData | FileSystemRights.ReadAttributes, AccessControlType.Deny);
        _folderRule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var fileSecurity = _file.GetAccessControl();
        fileSecurity.AddAccessRule(_fileRule);
        _file.SetAccessControl(fileSecurity);
        var folderSecurity = _folder.GetAccessControl();
        folderSecurity.AddAccessRule(_folderRule);
        _folder.SetAccessControl(folderSecurity);
    }

    public void Dispose()
    {
        var folderSecurity = _folder.GetAccessControl();
        folderSecurity.RemoveAccessRule(_folderRule);
        _folder.SetAccessControl(folderSecurity);
        var fileSecurity = _file.GetAccessControl();
        fileSecurity.RemoveAccessRule(_fileRule);
        _file.SetAccessControl(fileSecurity);
    }
}
