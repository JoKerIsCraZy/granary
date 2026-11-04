namespace Granary.Core;

/// <summary>
/// Windows' own programs Granary opens files and folders with, by their full path: a bare name would be looked up in the
/// app's folder and the PATH first, where another program of that name could stand.
/// </summary>
public static class WindowsPrograms
{
    public static string Notepad => Path.Combine(Environment.SystemDirectory, "notepad.exe");

    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
}
