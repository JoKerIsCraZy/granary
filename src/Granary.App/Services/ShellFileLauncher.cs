using System.ComponentModel;
using System.Diagnostics;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary><see cref="IFileLauncher"/> through the Windows shell.</summary>
internal sealed class ShellFileLauncher(ILogger<ShellFileLauncher> logger) : IFileLauncher
{
    // ERROR_NO_ASSOCIATION: Windows has no app for the file's type.
    private const int NoAssociation = 1155;

    // The app the viewer picked for the file's type. Windows has none for .conf by default, and then Notepad opens it.
    public bool OpenFile(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NoAssociation)
        {
            return Start("notepad.exe", path);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not open {File}", Path.GetFileName(path));
            return false;
        }
    }

    public bool OpenFolder(string path) => Directory.Exists(path) && Start("explorer.exe", path);

    private bool Start(string program, string path)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not open {Program}", program);
            return false;
        }
    }
}
