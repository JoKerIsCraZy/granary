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
    public async Task WriteConfig_MakesTheFolder_AndReplacesTheFileWhole()
    {
        var folder = Create();

        Assert.True(folder.WriteConfig("deband=yes\n"));
        Assert.True(folder.WriteConfig("# ünïcode\r\nscale=spline36\r\n"));

        Assert.Equal("# ünïcode\r\nscale=spline36\r\n", await File.ReadAllTextAsync(folder.ConfigFile, TestContext.Current.CancellationToken));
        Assert.Equal(new MpvConfigContent("# ünïcode\r\nscale=spline36\r\n", IsNew: false), folder.ReadConfig());
        Assert.Equal(["mpv.conf"], Directory.GetFiles(folder.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void AnExistingFileThatCannotBeRead_IsNotTakenForAMissingOne()
    {
        var folder = Create();
        Assert.True(folder.WriteConfig("deband=yes\n"));

        MpvConfigContent? read;
        using (new DeniedRead(folder.ConfigFile))
        {
            // Windows says it is not there.
            Assert.False(File.Exists(folder.ConfigFile));
            read = folder.ReadConfig();
        }

        Assert.Null(read);
        Assert.Contains(_logs.Lines, line => line.Contains("mpv.conf could not be read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'#', (byte)' ', 0xE9, (byte)'\n' })]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'#', 0, (byte)'\n', 0 })]
    [InlineData(new byte[] { (byte)'#', 0, (byte)'\n', 0 })]
    public void AFileThatIsNotUtf8_IsNotText_AndStaysAsItIs(byte[] file)
    {
        var folder = Create();
        Directory.CreateDirectory(folder.Directory);
        File.WriteAllBytes(folder.ConfigFile, file);

        var read = folder.ReadConfig();

        Assert.Equal(new MpvConfigContent("", IsNew: false, IsNotText: true), read);
        Assert.Equal(file, File.ReadAllBytes(folder.ConfigFile));
    }

    [Fact]
    public void AUtf8File_ReadsAsItsText_WithOrWithoutAByteOrderMark()
    {
        var folder = Create();
        Directory.CreateDirectory(folder.Directory);
        File.WriteAllBytes(folder.ConfigFile, "# ünïcode\n"u8.ToArray());
        Assert.Equal(new MpvConfigContent("# ünïcode\n", IsNew: false), folder.ReadConfig());

        File.WriteAllBytes(folder.ConfigFile, [0xEF, 0xBB, 0xBF, .. "# ünïcode\n"u8]);
        Assert.Equal(new MpvConfigContent("# ünïcode\n", IsNew: false), folder.ReadConfig());
    }

    [Fact]
    public void ALeftoverOfAnEarlierWrite_DoesNotGetInTheWay()
    {
        var folder = Create();
        // A folder under the name a fixed temporary file would have.
        Directory.CreateDirectory(folder.ConfigFile + ".tmp");

        Assert.True(folder.WriteConfig("deband=yes\n"));

        Assert.Equal("deband=yes\n", File.ReadAllText(folder.ConfigFile));
        Assert.Equal(["mpv.conf"], Directory.GetFiles(folder.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void AWriteThatCannotReplaceTheFile_LeavesItAndNothingBeside()
    {
        var folder = Create();
        Assert.True(folder.WriteConfig("deband=yes\n"));

        using (new FileStream(folder.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(folder.WriteConfig("deband=no\n"));
        }

        Assert.Equal("deband=yes\n", File.ReadAllText(folder.ConfigFile));
        Assert.Equal(["mpv.conf"], Directory.GetFiles(folder.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void ReadConfig_WithoutAFile_IsTheTemplate_NotWritten()
    {
        var folder = Create();

        Assert.Equal(new MpvConfigContent(MpvConfigFolder.Template.ReplaceLineEndings("\r\n"), IsNew: true), folder.ReadConfig());
        Assert.False(Directory.Exists(folder.Directory));
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
