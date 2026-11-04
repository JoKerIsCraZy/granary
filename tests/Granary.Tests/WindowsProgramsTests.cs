using Granary.Core;

namespace Granary.Tests;

/// <summary>Windows' own programs, by full path, so no program of the same name elsewhere is started instead.</summary>
public sealed class WindowsProgramsTests
{
    [Fact]
    public void NotepadAndExplorer_AreWindowsOwn_ByTheirFullPath()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "notepad.exe"), WindowsPrograms.Notepad);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), WindowsPrograms.Explorer);
        Assert.All([WindowsPrograms.Notepad, WindowsPrograms.Explorer], path =>
        {
            Assert.True(Path.IsPathFullyQualified(path), path);
            Assert.True(File.Exists(path), path);
        });
    }
}
