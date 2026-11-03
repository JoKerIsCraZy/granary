using Granary.Core.Playback;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;

namespace Granary.Tests.Settings;

/// <summary>Settings → Playback → This PC, below "mpv configuration": what Granary's editor changed, and the presets.</summary>
public sealed class MpvSettingsViewModelTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeMpvRestart _player = new();
    private readonly MpvConfigChanges _changes;
    private readonly FakeConfirmation _confirmation = new();
    private readonly MpvConfigFolder _folder;
    private readonly MpvPresets _presets;

    public MpvSettingsViewModelTests()
    {
        var logs = new LogSink();
        _changes = new MpvConfigChanges(_player, new ImmediateDispatcher());
        _folder = new MpvConfigFolder(Path.Combine(_data, "mpv"), logs.For<MpvConfigFolder>());
        _presets = new MpvPresets(_folder, logs.For<MpvPresets>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private MpvSettingsViewModel Row() => new(_folder, _presets, _changes, _confirmation);

    private async Task<MpvSettingsViewModel> Loaded()
    {
        var row = Row();
        await row.LoadPresetsAsync();
        return row;
    }

    private static string[] Names(MpvSettingsViewModel row) => [.. row.Presets.Select(p => p.Name)];

    [Fact]
    public void ASave_OffersApplyNow()
    {
        using var row = Row();
        Assert.Equal("", row.ChangeText);
        Assert.False(row.ShowsApplyNow);

        _changes.Saved();

        Assert.Equal("Your changes to mpv.conf apply after you restart Granary, or with Apply now.", row.ChangeText);
        Assert.True(row.ShowsApplyNow);
    }

    [Fact]
    public async Task ApplyNow_WithNothingPlaying_StartsThePlayerAgain_AndTheLineGoes()
    {
        using var row = Row();
        _changes.Saved();

        await row.ApplyNowCommand.ExecuteAsync(null);

        Assert.Equal(1, _player.Restarts);
        Assert.Equal("", row.ChangeText);
        Assert.False(row.ShowsApplyNow);
    }

    [Fact]
    public async Task ApplyNow_WhileATitlePlays_AppliesToTheNextTitle()
    {
        using var row = Row();
        _changes.Saved();
        _player.IsPlaying = true;

        await row.ApplyNowCommand.ExecuteAsync(null);

        Assert.Equal(0, _player.Restarts);
        Assert.Equal("Applies to the next title.", row.ChangeText);
        Assert.False(row.ShowsApplyNow);

        _player.TitleEnds();

        Assert.Equal(1, _player.Restarts);
        Assert.Equal("", row.ChangeText);
    }

    [Fact]
    public void ASave_BeforeThePlayerFirstStarted_AppliesToTheNextTitle()
    {
        _player.IsRunning = false;
        using var row = Row();

        _changes.Saved();

        Assert.Equal("Applies to the next title.", row.ChangeText);
        Assert.False(row.ShowsApplyNow);
    }

    [Fact]
    public void ASaveBeforeThePageOpened_IsSaidToo()
    {
        _changes.Saved();

        using var row = Row();

        Assert.Equal(MpvSettingsViewModel.PendingText, row.ChangeText);
    }

    [Fact]
    public void APageThatLeft_StopsFollowing()
    {
        var row = Row();

        row.Dispose();
        _changes.Saved();

        Assert.Equal("", row.ChangeText);
    }

    [Fact]
    public async Task ThePresets_AreListedByName()
    {
        _presets.Save("low power", "a");
        _presets.Save("HQ", "b");

        using var row = await Loaded();

        Assert.Equal(["HQ", "low power"], Names(row));
        Assert.True(row.HasPresets);
        Assert.Equal("Load the preset HQ", row.Presets[0].LoadName);
        Assert.Equal("Delete the preset HQ", row.Presets[0].DeleteName);
    }

    [Fact]
    public async Task WithoutPresets_ThereAreNone()
    {
        using var row = await Loaded();

        Assert.Empty(row.Presets);
        Assert.False(row.HasPresets);
    }

    [Fact]
    public async Task SaveAsPreset_KeepsTheCurrentMpvConf_UnderASafeName()
    {
        _folder.WriteConfig("# mine\r\ndeband=yes\r\n");
        using var row = await Loaded();

        await row.SavePresetAsync("  HQ: night/day  ");

        Assert.Equal("# mine\r\ndeband=yes\r\n", _presets.Read("HQ nightday"));
        Assert.Equal(["HQ nightday"], Names(row));
        Assert.Equal("Saved your mpv.conf as HQ nightday.", row.PresetMessage);
        Assert.Empty(_confirmation.Asked);
        // Nothing changed for the player.
        Assert.False(_changes.IsPending);
    }

    [Fact]
    public async Task SaveAsPreset_OverAnExistingName_AsksFirst()
    {
        _folder.WriteConfig("deband=no\n");
        _presets.Save("HQ", "deband=yes\n");
        using var row = await Loaded();
        _confirmation.Answer = false;

        await row.SavePresetAsync("hq");

        Assert.Equal([("Replace the preset hq?", "A preset of that name is already saved. Saving replaces it with your mpv.conf.", "Replace")],
            _confirmation.Asked);
        Assert.Equal("deband=yes\n", _presets.Read("HQ"));
        Assert.Equal("", row.PresetMessage);

        _confirmation.Answer = true;
        await row.SavePresetAsync("HQ");

        Assert.Equal("deband=no\n", _presets.Read("HQ"));
        Assert.Equal(["HQ"], Names(row));
    }

    [Fact]
    public async Task SaveAsPreset_WithoutAnMpvConf_SaysThereIsNone()
    {
        using var row = await Loaded();

        await row.SavePresetAsync("HQ");

        Assert.Equal("There's no mpv.conf to save yet.", row.PresetMessage);
        Assert.Empty(_presets.Names());
    }

    [Fact]
    public async Task SaveAsPreset_WithANameWindowsKeeps_SavesNothing()
    {
        _folder.WriteConfig("deband=yes\n");
        using var row = await Loaded();

        await row.SavePresetAsync("NUL");

        Assert.Empty(_presets.Names());
        Assert.Equal("Windows keeps this name for itself. Choose another.", row.PresetMessage);
    }

    [Theory]
    [InlineData("HQ", "")]
    [InlineData(" HQ: night ", "Saves as HQ night.")]
    [InlineData("  HQ  ", "")]
    [InlineData("", "Enter a name.")]
    [InlineData(":/", "Enter a name.")]
    [InlineData("con", "Windows keeps this name for itself. Choose another.")]
    public void TheNameField_SaysWhatANameBecomes(string typed, string note)
    {
        Assert.Equal(note, MpvSettingsViewModel.PresetNameNote(typed));
        Assert.Equal(MpvPresets.SafeName(typed) is not null, MpvSettingsViewModel.CanSavePresetAs(typed));
    }

    [Fact]
    public async Task LoadPreset_AsksFirst_ThenReplacesMpvConf_AndTheChangeIsPending()
    {
        _folder.WriteConfig("deband=no\n");
        _presets.Save("HQ", "# hq\r\nscale=ewa_lanczossharp\r\n");
        using var row = await Loaded();

        await row.LoadPresetAsync(row.Presets[0]);

        Assert.Equal([("Load the preset HQ?", "It replaces your mpv.conf. Save mpv.conf as a preset first to keep it.", "Load")], _confirmation.Asked);
        Assert.Equal("# hq\r\nscale=ewa_lanczossharp\r\n", await File.ReadAllTextAsync(_folder.ConfigFile, TestContext.Current.CancellationToken));
        Assert.Equal("Loaded HQ into mpv.conf.", row.PresetMessage);
        Assert.True(_changes.IsPending);
        Assert.Equal(MpvSettingsViewModel.PendingText, row.ChangeText);
    }

    [Fact]
    public async Task LoadPreset_Cancelled_LeavesMpvConf()
    {
        _folder.WriteConfig("deband=no\n");
        _presets.Save("HQ", "deband=yes\n");
        using var row = await Loaded();
        _confirmation.Answer = false;

        await row.LoadPresetAsync(row.Presets[0]);

        Assert.Equal("deband=no\n", await File.ReadAllTextAsync(_folder.ConfigFile, TestContext.Current.CancellationToken));
        Assert.False(_changes.IsPending);
    }

    [Fact]
    public async Task APresetGoneMeanwhile_IsSaid_AndTheListFollows()
    {
        _folder.WriteConfig("deband=no\n");
        _presets.Save("HQ", "deband=yes\n");
        using var row = await Loaded();
        File.Delete(Path.Combine(_presets.Directory, "HQ.conf"));

        await row.LoadPresetAsync(row.Presets[0]);

        Assert.Equal("Couldn't load the preset HQ.", row.PresetMessage);
        Assert.Equal("deband=no\n", await File.ReadAllTextAsync(_folder.ConfigFile, TestContext.Current.CancellationToken));
        Assert.Empty(row.Presets);
        Assert.False(_changes.IsPending);
    }

    [Fact]
    public async Task ALoadedPreset_IsNamed_AndAnEditMarksItChanged()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        using var row = await Loaded();
        Assert.Equal("", row.ActivePresetText);
        Assert.False(row.HasActivePreset);

        await row.LoadPresetAsync(row.Presets[0]);

        Assert.Equal("Preset: HQ", row.ActivePresetText);
        Assert.True(row.HasActivePreset);

        // As Granary's editor saves.
        _folder.WriteConfig("deband=yes\nscale=spline36\n");
        _changes.Saved();
        await row.LoadPresetsAsync();

        Assert.Equal("Preset: HQ (changed)", row.ActivePresetText);
    }

    [Fact]
    public async Task Unload_AsksFirst_ThenBringsBackTheOriginal_AndTheChangeIsPending()
    {
        _folder.WriteConfig("# mine\r\n");
        _presets.Save("A", "deband=yes\n");
        _presets.Save("B", "deband=no\n");
        using var row = await Loaded();
        await row.LoadPresetAsync(row.Presets[0]);
        await row.LoadPresetAsync(row.Presets[1]);
        _confirmation.Asked.Clear();
        _confirmation.Answer = false;

        await row.UnloadPresetAsync();

        Assert.Equal([("Go back to your mpv.conf from before you loaded a preset?", "Changes made since are lost.", "Unload")], _confirmation.Asked);
        Assert.Equal("deband=no\n", await File.ReadAllTextAsync(_folder.ConfigFile, TestContext.Current.CancellationToken));
        Assert.Equal("Preset: B", row.ActivePresetText);

        _confirmation.Answer = true;
        await row.UnloadPresetAsync();

        Assert.Equal("# mine\r\n", await File.ReadAllTextAsync(_folder.ConfigFile, TestContext.Current.CancellationToken));
        Assert.Equal("Your mpv.conf from before the preset is back.", row.PresetMessage);
        Assert.Equal("", row.ActivePresetText);
        Assert.False(row.HasActivePreset);
        Assert.Equal(MpvSettingsViewModel.PendingText, row.ChangeText);
    }

    [Fact]
    public async Task Unload_WithoutAnMpvConfBefore_RemovesIt()
    {
        _presets.Save("HQ", "deband=yes\n");
        using var row = await Loaded();
        await row.LoadPresetAsync(row.Presets[0]);

        await row.UnloadPresetAsync();

        Assert.False(File.Exists(_folder.ConfigFile));
        Assert.Equal("You had no mpv.conf before the preset, so it is removed.", row.PresetMessage);
        Assert.Equal("", row.ActivePresetText);
    }

    [Fact]
    public async Task DeletingTheLoadedPreset_KeepsItNamed_AndUnloadStillWorks()
    {
        _folder.WriteConfig("# mine\n");
        _presets.Save("HQ", "deband=yes\n");
        using var row = await Loaded();
        await row.LoadPresetAsync(row.Presets[0]);

        await row.DeletePresetAsync(row.Presets[0]);

        Assert.Empty(row.Presets);
        Assert.Equal("Preset: HQ", row.ActivePresetText);

        await row.UnloadPresetAsync();

        Assert.Equal("# mine\n", await File.ReadAllTextAsync(_folder.ConfigFile, TestContext.Current.CancellationToken));
        Assert.Equal("", row.ActivePresetText);
    }

    [Fact]
    public async Task DeletePreset_AsksFirst_ThenRemovesIt()
    {
        _presets.Save("HQ", "deband=yes\n");
        _presets.Save("low", "deband=no\n");
        using var row = await Loaded();
        _confirmation.Answer = false;

        await row.DeletePresetAsync(row.Presets[0]);

        Assert.Equal([("Delete the preset HQ?", "Its file goes from the presets folder. This can't be undone.", "Delete")], _confirmation.Asked);
        Assert.Equal(["HQ", "low"], Names(row));

        _confirmation.Answer = true;
        await row.DeletePresetAsync(row.Presets[0]);

        Assert.Equal(["low"], Names(row));
        Assert.Equal(["low"], _presets.Names());
        Assert.Equal("Deleted the preset HQ.", row.PresetMessage);
    }
}
