using Granary.Core;
using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>The folder of the viewer's own mpv.conf, and the file Settings makes in it.</summary>
public sealed class MpvConfigFolderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private MpvConfigFolder Create() => new(Path.Combine(_directory, "mpv"), _logs.For<MpvConfigFolder>());

    [Fact]
    public void TheFolder_IsMpvInTheDataDirectory()
    {
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "mpv"), MpvConfigFolder.DefaultDirectory);
        Assert.Equal(Path.Combine(_directory, "mpv", "mpv.conf"), Create().ConfigFile);
    }

    [Fact]
    public async Task AMissingFile_IsMadeWithCommentsOnly()
    {
        var folder = Create();

        string? file = folder.EnsureConfigFile();

        Assert.Equal(folder.ConfigFile, file);
        string[] lines = await File.ReadAllLinesAsync(folder.ConfigFile, TestContext.Current.CancellationToken);
        Assert.NotEmpty(lines);
        // mpv reads nothing from it: every line is a comment or empty.
        Assert.All(lines, line => Assert.True(line.Length == 0 || line.StartsWith('#'), line));
        Assert.Contains(lines, line => line.Contains("restart Granary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExistingFile_IsLeftAsItIs()
    {
        var folder = Create();
        Directory.CreateDirectory(folder.Directory);
        await File.WriteAllTextAsync(folder.ConfigFile, "deband=yes\n", TestContext.Current.CancellationToken);

        Assert.Equal(folder.ConfigFile, folder.EnsureConfigFile());

        Assert.Equal("deband=yes\n", await File.ReadAllTextAsync(folder.ConfigFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AFolderNamedMpvConf_IsNotAFileToOpen()
    {
        var folder = Create();
        Directory.CreateDirectory(folder.ConfigFile);

        Assert.True(folder.ConfigFileIsFolder);
        Assert.Null(folder.EnsureConfigFile());
        Assert.True(Directory.Exists(folder.ConfigFile));
    }

    [Fact]
    public void AWriteThatFails_LeavesNoEmptyMpvConf()
    {
        var folder = Create();
        // A folder where the file is written first.
        Directory.CreateDirectory(folder.ConfigFile + ".tmp");

        Assert.Null(folder.EnsureConfigFile());

        Assert.False(File.Exists(folder.ConfigFile));
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning ", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderThatCannotBeMade_IsSaidInTheLog()
    {
        // A file where the folder belongs.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "mpv"), "");
        var folder = Create();

        Assert.False(folder.EnsureDirectory());
        Assert.Null(folder.EnsureConfigFile());
        Assert.Contains(_logs.Lines, line => line.StartsWith("Warning ", StringComparison.Ordinal));
    }
}
