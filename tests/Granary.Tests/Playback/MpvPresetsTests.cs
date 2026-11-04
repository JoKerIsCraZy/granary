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

        // Another program has the file open: it cannot be replaced.
        using (new FileStream(_folder.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(_presets.Load("HQ"));
        }

        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void APresetThatIsNotUtf8_IsCopiedToMpvConfByteForByte()
    {
        byte[] preset = [(byte)'#', (byte)' ', 0xE9, (byte)'\n'];
        _presets.Save("HQ", "");
        File.WriteAllBytes(Path.Combine(_presets.Directory, "HQ.conf"), preset);

        Assert.True(_presets.Load("HQ"));

        Assert.Equal(preset, MpvConfBytes());
        Assert.Equal(new MpvActivePreset("HQ", IsChanged: false), _presets.Active());
    }

    [Fact]
    public void AnExistingMpvConfThatCannotBeRead_IsNotBackedUpAsMissing_AndLoadsNothing()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");

        using (new DeniedRead(_folder.ConfigFile))
        {
            Assert.False(_presets.Load("HQ"));
        }

        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.False(File.Exists(Path.Combine(_presets.Directory, ".before-preset.none")));
        Assert.Null(_presets.Active());
    }

    [Fact]
    public void AnUnloadThatCannotRemoveItsMark_StillEndsThePreset_AndAFileMadeSinceIsSafe()
    {
        _presets.Save("HQ", "deband=yes\n");
        _presets.Load("HQ");
        string mark = Path.Combine(_presets.Directory, ".before-preset.none");

        using (new FileStream(mark, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(MpvPresetUnload.Removed, _presets.Unload());
        }

        Assert.False(File.Exists(_folder.ConfigFile));
        Assert.Null(_presets.Active());

        _folder.WriteConfig("# made since\n");
        Assert.True(_presets.Load("HQ"));
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());

        Assert.Equal("# made since\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void AnUnloadThatCannotRemoveItsBackup_StillEndsThePreset_AndAFileMadeSinceIsSafe()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        _presets.Load("HQ");
        string backup = Path.Combine(_presets.Directory, ".before-preset.conf");

        using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());
        }

        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());

        _folder.WriteConfig("# made since\n");
        Assert.True(_presets.Load("HQ"));
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());

        Assert.Equal("# made since\n", File.ReadAllText(_folder.ConfigFile));
    }

    [Fact]
    public void AnUnloadThatCannotRemoveTheName_IsNoSuccess_AndAFileMadeSinceIsSafe()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        _presets.Load("HQ");

        using (new FileStream(Path.Combine(_presets.Directory, ".active.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(MpvPresetUnload.NotCleanedUp, _presets.Unload());
        }

        // mpv.conf is back, and the way to finish stays offered.
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.NotNull(_presets.Active());

        _folder.WriteConfig("# made since\n");
        Assert.True(_presets.Load("HQ"));
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());

        Assert.Equal("# made since\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());
    }

    [Theory]
    [InlineData(".before-preset.none")]
    [InlineData(".before-preset.conf")]
    public void WhatAnEarlierSessionLeftBehind_IsNotTrusted_ByTheNextLoad(string leftover)
    {
        _folder.WriteConfig("# mine, made since\n");
        _presets.Save("HQ", "deband=yes\n");
        // Left behind, with no preset loaded: its Unload could not remove it.
        File.WriteAllText(Path.Combine(_presets.Directory, leftover), "# from earlier\n");

        Assert.True(_presets.Load("HQ"));
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());

        Assert.Equal("# mine, made since\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void WhatCannotBeRemovedOfAnEarlierSession_StopsTheLoad_AsItWouldMisleadUnload()
    {
        _folder.WriteConfig("# mine, made since\n");
        _presets.Save("HQ", "deband=yes\n");
        string mark = Path.Combine(_presets.Directory, ".before-preset.none");
        File.WriteAllText(mark, "");

        using (new FileStream(mark, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_presets.Load("HQ"));
        }

        Assert.Equal("# mine, made since\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());
    }

    [Fact]
    public void AFirstLoadThatCannotNameThePreset_NeverReplacesMpvConf_AndReportsTheFailure()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_folder.ConfigFile, written);
        // A folder where the name of the preset in use is kept.
        Directory.CreateDirectory(Path.Combine(_presets.Directory, ".active.json"));

        Assert.False(_presets.Load("HQ"));

        Assert.Equal(written, File.GetLastWriteTimeUtc(_folder.ConfigFile));
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void AFirstLoadThatCannotNameThePreset_WithoutAnMpvConfBefore_LeavesNone()
    {
        _presets.Save("HQ", "deband=yes\n");
        Directory.CreateDirectory(Path.Combine(_presets.Directory, ".active.json"));

        Assert.False(_presets.Load("HQ"));

        Assert.False(File.Exists(_folder.ConfigFile));
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void ASecondLoadThatCannotNameThePreset_KeepsTheFirst_AndTheWayBack()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("A", "deband=yes\n");
        _presets.Save("B", "deband=no\n");
        Assert.True(_presets.Load("A"));

        using (new FileStream(Path.Combine(_presets.Directory, ".active.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_presets.Load("B"));
        }

        Assert.Equal("deband=yes\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(new MpvActivePreset("A", IsChanged: false), _presets.Active());
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
    }

    [Fact]
    public void AFirstLoadWhoseMpvConfCannotBeReplaced_LeavesNothingBehind()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");

        using (new FileStream(_folder.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(_presets.Load("HQ"));
        }

        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Null(_presets.Active());
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void ASecondLoadWhoseMpvConfCannotBeReplaced_KeepsTheFirstPresetNamed_AndTheWayBack()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("A", "deband=yes\n");
        _presets.Save("B", "deband=no\n");
        Assert.True(_presets.Load("A"));

        using (new FileStream(_folder.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(_presets.Load("B"));
        }

        Assert.Equal("deband=yes\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(new MpvActivePreset("A", IsChanged: false), _presets.Active());
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
    }

    [Fact]
    public void ALoadThatCanNeitherFinishNorTakeBackTheName_KeepsTheWayBack()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        FileStream? config = null;
        FileStream? name = null;
        _presets.BeforeConfigWrite = () =>
        {
            config = new FileStream(_folder.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            name = new FileStream(Path.Combine(_presets.Directory, ".active.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        };

        Assert.False(_presets.Load("HQ"));
        config!.Dispose();
        name!.Dispose();

        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(new MpvActivePreset("HQ", IsChanged: true), _presets.Active());
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(["HQ.conf"], Directory.GetFiles(_presets.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public void ALoadThatCannotLookAtTheName_ChangesNothing_AndTheWayBackStays()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("A", "deband=yes\n");
        _presets.Save("B", "deband=no\n");
        Assert.True(_presets.Load("A"));

        using (new DeniedRead(Path.Combine(_presets.Directory, ".active.json")))
        {
            Assert.False(_presets.Load("B"));
        }

        Assert.Equal("deband=yes\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(new MpvActivePreset("A", IsChanged: false), _presets.Active());
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
    }

    [Fact]
    public void ALoadThatCannotLookAtTheBackup_ChangesNothing_AndTheWayBackStays()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("A", "deband=yes\n");
        _presets.Save("B", "deband=no\n");
        Assert.True(_presets.Load("A"));

        using (new DeniedRead(Path.Combine(_presets.Directory, ".before-preset.conf")))
        {
            Assert.False(_presets.Load("B"));
        }

        Assert.Equal("deband=yes\n", File.ReadAllText(_folder.ConfigFile));
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
    }

    [Fact]
    public void AnUnloadThatCannotLookAtTheBackup_Fails_AndKeepsIt()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        Assert.True(_presets.Load("HQ"));

        using (new DeniedRead(Path.Combine(_presets.Directory, ".before-preset.conf")))
        {
            Assert.Equal(MpvPresetUnload.Failed, _presets.Unload());
        }

        Assert.Equal("deband=yes\n", File.ReadAllText(_folder.ConfigFile));
        Assert.NotNull(_presets.Active());
        Assert.Equal(MpvPresetUnload.Restored, _presets.Unload());
        Assert.Equal("# mine\n", File.ReadAllText(_folder.ConfigFile));
    }

    [Fact]
    public void WhileMpvConfCannotBeRead_ThePresetStaysOnShow_SoUnloadIsOffered()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        Assert.True(_presets.Load("HQ"));

        using (new FileStream(_folder.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(new MpvActivePreset("HQ", IsChanged: true), _presets.Active());
        }

        Assert.Equal(new MpvActivePreset("HQ", IsChanged: false), _presets.Active());
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
