using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>mpv.conf presets: plain files in the mpv folder's presets folder, under names safe for Windows.</summary>
public sealed class MpvPresetsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly MpvConfigFolder _folder;
    private readonly MpvPresets _presets;

    public MpvPresetsTests()
    {
        var logs = new LogSink();
        _folder = new MpvConfigFolder(Path.Combine(_directory, "mpv"), logs.For<MpvConfigFolder>());
        _presets = new MpvPresets(_folder, logs.For<MpvPresets>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("HQ shaders", "HQ shaders")]
    [InlineData("  Anime 4K  ", "Anime 4K")]
    [InlineData("night/day\\mix", "nightdaymix")]
    [InlineData("..\\..\\mpv", "mpv")]
    [InlineData("HDR: tone map?", "HDR tone map")]
    [InlineData("low power.", "low power")]
    [InlineData("v1.2 sharp", "v1.2 sharp")]
    [InlineData("Console", "Console")]
    [InlineData("COM10", "COM10")]
    public void ANameBecomesASafeFileName(string typed, string safe) => Assert.Equal(safe, MpvPresets.SafeName(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>|*")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("Aux.backup")]
    [InlineData("com1")]
    [InlineData("LPT9")]
    [InlineData("COM¹")]
    public void ANameWithNothingSafeLeft_OrOneWindowsKeeps_IsNone(string typed) => Assert.Null(MpvPresets.SafeName(typed));

    [Fact]
    public void ALongName_IsCutToItsLimit()
    {
        Assert.Equal(new string('a', MpvPresets.MaxNameLength), MpvPresets.SafeName(new string('a', 100)));
        // A cut that ends in a space loses it too.
        Assert.Equal(new string('a', MpvPresets.MaxNameLength - 1), MpvPresets.SafeName(new string('a', MpvPresets.MaxNameLength - 1) + " bcdef"));
    }

    [Fact]
    public void Presets_ArePlainFiles_ListedByName()
    {
        Assert.Empty(_presets.Names());

        Assert.True(_presets.Save("low power", "profile=fast\r\n"));
        Assert.True(_presets.Save("HQ", "# best\r\nscale=ewa_lanczossharp\r\n"));

        Assert.Equal(Path.Combine(_folder.Directory, "presets"), _presets.Directory);
        Assert.True(File.Exists(Path.Combine(_presets.Directory, "HQ.conf")));
        Assert.Equal(["HQ", "low power"], _presets.Names());
        Assert.True(_presets.Exists("hq"));
        Assert.Equal("# best\r\nscale=ewa_lanczossharp\r\n", _presets.Read("HQ"));
    }

    [Fact]
    public void Save_ReplacesAPresetWhole()
    {
        _presets.Save("HQ", "deband=yes\n");

        Assert.True(_presets.Save("HQ", "deband=no\n"));

        Assert.Equal("deband=no\n", _presets.Read("HQ"));
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void Delete_RemovesItsFile()
    {
        _presets.Save("HQ", "deband=yes\n");

        Assert.True(_presets.Delete("HQ"));

        Assert.Empty(_presets.Names());
        Assert.Null(_presets.Read("HQ"));
    }

    [Fact]
    public void OnlyConfFiles_ArePresets()
    {
        Directory.CreateDirectory(_presets.Directory);
        File.WriteAllText(Path.Combine(_presets.Directory, "notes.txt"), "");
        File.WriteAllText(Path.Combine(_presets.Directory, "half.conf.tmp"), "");
        File.WriteAllText(Path.Combine(_presets.Directory, "mine.conf"), "");
        Directory.CreateDirectory(Path.Combine(_presets.Directory, "folder.conf"));

        Assert.Equal(["mine"], _presets.Names());
    }

    private byte[] MpvConfBytes() => File.ReadAllBytes(_folder.ConfigFile);

    [Fact]
    public void LoadThenUnload_RestoresTheExactOriginal()
    {
        // A byte order mark, CRLF and a character outside ASCII: the original comes back byte for byte.
        byte[] original = [0xEF, 0xBB, 0xBF, .. "# mine é\r\ndeband=yes\r\n"u8];
        Directory.CreateDirectory(_folder.Directory);
        File.WriteAllBytes(_folder.ConfigFile, original);
        _presets.Save("HQ", "scale=ewa_lanczossharp\n");

        Assert.True(_presets.Load("HQ"));
        Assert.Equal("scale=ewa_lanczossharp\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(new MpvActivePreset("HQ", IsChanged: false), _presets.Active());

        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());

        Assert.Equal(original, MpvConfBytes());
        Assert.Null(_presets.Active());
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void LoadWithoutAFile_ThenUnload_RemovesMpvConf()
    {
        _presets.Save("HQ", "deband=yes\n");

        Assert.True(_presets.Load("HQ"));
        Assert.True(File.Exists(_folder.ConfigFile));

        Assert.Equal(MpvPresetUnload.Removed, _presets.Unload());

        Assert.False(File.Exists(_folder.ConfigFile));
        Assert.Null(_presets.Active());
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void LoadAThenB_ThenUnload_GivesBackTheOriginal_NotA()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("A", "deband=yes\n");
        _presets.Save("B", "deband=no\n");

        Assert.True(_presets.Load("A"));
        Assert.True(_presets.Load("B"));
        Assert.Equal(new MpvActivePreset("B", IsChanged: false), _presets.Active());

        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());

        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
    }

    [Fact]
    public void LoadWithoutAFile_ThenAnother_StillRemovesMpvConfOnUnload()
    {
        _presets.Save("A", "deband=yes\n");
        _presets.Save("B", "deband=no\n");

        _presets.Load("A");
        _presets.Load("B");

        Assert.Equal(MpvPresetUnload.Removed, _presets.Unload());
        Assert.False(File.Exists(_folder.ConfigFile));
    }

    [Fact]
    public void AnEditAfterALoad_ShowsTheActivePresetAsChanged()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        _presets.Load("HQ");

        _folder.WriteConfig("deband=yes\nscale=spline36\n");

        Assert.Equal(new MpvActivePreset("HQ", IsChanged: true), _presets.Active());

        // Back to the preset's text, it is the preset again.
        _folder.WriteConfig("deband=yes\n");
        Assert.Equal(new MpvActivePreset("HQ", IsChanged: false), _presets.Active());
    }

    [Fact]
    public void ThePresetsFileDeleted_LeavesItActive_AndUnloadStillWorks()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        _presets.Load("HQ");

        Assert.True(_presets.Delete("HQ"));

        Assert.Equal(new MpvActivePreset("HQ", IsChanged: false), _presets.Active());
        Assert.Empty(_presets.Names());
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
    }

    [Fact]
    public void TheBackupAndTheMarkers_AreNoPresets()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");

        _presets.Load("HQ");

        Assert.True(File.Exists(Path.Combine(_presets.Directory, ".before-preset.conf")));
        Assert.True(File.Exists(Path.Combine(_presets.Directory, ".active.json")));
        Assert.Equal(["HQ"], _presets.Names());
        Assert.Null(_presets.Read(".before-preset"));
        Assert.False(_presets.Delete(".active"));
    }

    [Fact]
    public void APresetThatCannotBeRead_LoadsNothing_AndKeepsNoBackup()
    {
        _folder.WriteConfig("# mine\n");

        Assert.False(_presets.Load("HQ"));

        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());
        Assert.False(Directory.Exists(_presets.Directory) && Directory.EnumerateFiles(_presets.Directory).Any());
    }

    [Fact]
    public void AnMpvConfThatCannotBeWritten_LeavesNoBackupBehind()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        // A folder where mpv.conf is written first.
        Directory.CreateDirectory(_folder.ConfigFile + ".tmp");

        Assert.False(_presets.Load("HQ"));

        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void UnloadWithoutABackup_HasNothingToRestore_AndForgetsThePreset()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        _presets.Load("HQ");
        File.Delete(Path.Combine(_presets.Directory, ".before-preset.conf"));

        Assert.Equal(MpvPresetUnload.NothingToRestore, _presets.Unload());

        Assert.Equal("deband=yes\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());
    }

    [Theory]
    [InlineData("../mpv")]
    [InlineData("..\\mpv")]
    [InlineData("C:\\mpv")]
    [InlineData("CON")]
    [InlineData("")]
    public void ANameThatIsNoFileName_NeverReachesAPath(string name)
    {
        _folder.WriteConfig("deband=yes\n");

        Assert.False(_presets.Save(name, "x"));
        Assert.Null(_presets.Read(name));
        Assert.False(_presets.Delete(name));
        Assert.False(_presets.Exists(name));

        Assert.Equal("deband=yes\n", File.ReadAllText(_folder.ConfigFile));
    }
}
