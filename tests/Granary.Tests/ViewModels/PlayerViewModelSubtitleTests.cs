using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;
using Granary.Tests.Settings;

namespace Granary.Tests.ViewModels;

/// <summary>The player's subtitle button, C, Add Subtitles and the Subtitle appearance panel.</summary>
public sealed class PlayerViewModelSubtitleTests
{
    private const string Track = "file:1:subtitle:0";

    private static readonly SubtitleInventoryItem English = new() { TrackId = Track, Source = "embedded", Language = "eng" };

    private readonly FakePlaybackEngine _engine = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly FakeProfileSettings _settings = new();

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel CreateViewModel() => new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
    {
        var playback = new FakeServerPlayback();
        _playbacks.Add(playback);
        return playback;
    }, new PlaybackStops(), settings: _settings);

    private async Task<PlayerViewModel> PlayingAsync()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        return viewModel;
    }

    private static string Json(SubtitleAppearance look) => look.ToJson().GetRawText();

    [Fact]
    public async Task TheButton_IsThereForAServerTitle_UntilItIsLeft()
    {
        var viewModel = await PlayingAsync();
        Assert.True(viewModel.HasSubtitleMenu);

        viewModel.Leave();

        Assert.False(viewModel.HasSubtitleMenu);
        Assert.Empty(viewModel.SubtitleTracks);
    }

    [Fact]
    public async Task TheDelay_WaitsWhileNoSubtitleShows()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseSubtitles([English], selected: null);
        Assert.False(viewModel.IsSubtitleOn);
        Assert.False(viewModel.CanDelayEarlier);
        Assert.False(viewModel.CanDelayLater);

        Playback.RaiseSubtitles([English], Track);

        Assert.True(viewModel.IsSubtitleOn);
        Assert.True(viewModel.CanDelayLater);
    }

    [Fact]
    public async Task C_TurnsTheSubtitleOff_AndBringsItBack()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseSubtitles([English], Track);

        viewModel.ToggleSubtitles();
        Playback.RaiseSubtitles([English], selected: null);
        viewModel.ToggleSubtitles();

        Assert.Equal([null, Track], Playback.SubtitlePicks);
    }

    [Fact]
    public async Task C_WithNothingToBringBack_DoesNothing()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseSubtitles([English], selected: null);

        viewModel.ToggleSubtitles();

        Assert.Empty(Playback.SubtitlePicks);
    }

    [Fact]
    public async Task ANewTitle_ForgetsTheTrackToBringBack()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseSubtitles([English], Track);

        await viewModel.StartAsync(PlayRequest.Resume("movie-2"));
        Playback.RaiseSubtitles([English], selected: null);
        viewModel.ToggleSubtitles();

        Assert.Empty(Playback.SubtitlePicks);
    }

    [Fact]
    public async Task AnAddedSubtitle_GoesToThePlayback_ForTheFileThatPlays()
    {
        var viewModel = await PlayingAsync();
        Playback.PlayingFileId = "42";

        viewModel.ShowAddedSubtitle("77");

        Assert.Equal("42", viewModel.SubtitleFileId);
        Assert.Equal(["77"], Playback.AddedSubtitles);
    }

    [Fact]
    public async Task TheAppearancePanel_OpensOnTheLookThatShows_ThenTheProfiles()
    {
        var viewModel = await PlayingAsync();
        var profiles = SubtitleAppearance.Default with { FontColor = "#facc15" };
        _settings.Set(SettingKeys.SubtitleAppearance, Json(profiles));

        viewModel.OpenAppearanceCommand.Execute(null);

        Assert.True(viewModel.IsAppearanceOpen);
        Assert.Equal(profiles, viewModel.Look.Appearance);
        // Changed on another device since the title started: the player takes it too.
        Assert.Equal(profiles, _engine.SubtitleLook);
        Assert.Equal(PlayerViewModel.AppearanceSavedText, viewModel.AppearanceStatus);
    }

    [Fact]
    public async Task AChange_ShowsAtOnce_AndIsSavedForTheProfile_ClearingThisDevicesLookOnce()
    {
        var viewModel = await PlayingAsync();
        _settings.Set(SettingKeys.SubtitleAppearance, Json(SubtitleAppearance.Default), "profile_device");
        viewModel.OpenAppearanceCommand.Execute(null);

        viewModel.Look.SelectedFontSize = viewModel.Look.FontSizeChoices.Single(c => c.Value == "xlarge");
        viewModel.Look.TextOutline = true;

        Assert.Equal(68, _engine.SubtitleLook?.FontSize);
        Assert.True(_engine.SubtitleLook?.TextOutline);
        Assert.Equal(2, _settings.Writes.Count);
        Assert.EndsWith("clearing the device", _settings.Writes[0], StringComparison.Ordinal);
        Assert.DoesNotContain("clearing the device", _settings.Writes[1], StringComparison.Ordinal);
        Assert.Contains("\"fontSize\":\"xlarge\"", _settings.Writes[1], StringComparison.Ordinal);
        Assert.Contains("\"textOutline\":true", _settings.Writes[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangesWhileASaveIsOnItsWay_SaveOnlyTheLatest()
    {
        var viewModel = await PlayingAsync();
        viewModel.OpenAppearanceCommand.Execute(null);
        _settings.Gate = new TaskCompletionSource();

        viewModel.Look.SelectedPosition = viewModel.Look.PositionChoices.Single(c => c.Value == "top");
        viewModel.Look.SelectedPosition = viewModel.Look.PositionChoices.Single(c => c.Value == "lower-third");
        viewModel.Look.SelectedPosition = viewModel.Look.PositionChoices.Single(c => c.Value == "bottom");
        _settings.Gate.SetResult();
        await Until(() => _settings.Writes.Count == 2);

        Assert.Contains("\"position\":\"top\"", _settings.Writes[0], StringComparison.Ordinal);
        Assert.Contains("\"position\":\"bottom\"", _settings.Writes[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedSave_SaysSo_AndKeepsTheLookOnScreen()
    {
        var viewModel = await PlayingAsync();
        viewModel.OpenAppearanceCommand.Execute(null);
        _settings.Refuses = true;

        viewModel.Look.PickFontColor("#22c55e");
        await Until(() => viewModel.AppearanceStatus != PlayerViewModel.AppearanceSavedText);

        Assert.Equal("Couldn't save. The next change tries again.", viewModel.AppearanceStatus);
        Assert.Equal("#22c55e", _engine.SubtitleLook?.FontColor);
    }

    [Fact]
    public async Task ALookTheServerDidNotTake_StaysWhenThePanelOpensAgain()
    {
        var viewModel = await PlayingAsync();
        _settings.Set(SettingKeys.SubtitleAppearance, Json(SubtitleAppearance.Default));
        viewModel.OpenAppearanceCommand.Execute(null);
        _settings.Refuses = true;
        viewModel.Look.PickFontColor("#22c55e");
        await Until(() => viewModel.AppearanceStatus != PlayerViewModel.AppearanceSavedText);

        viewModel.CloseAppearanceCommand.Execute(null);
        viewModel.OpenAppearanceCommand.Execute(null);

        Assert.Equal("#22c55e", viewModel.Look.Appearance.FontColor);
        Assert.Equal("#22c55e", _engine.SubtitleLook?.FontColor);
    }

    [Fact]
    public async Task UseFallbackStyle_ClearsTheStoredLooks_AndShowsTheServersDefault()
    {
        var viewModel = await PlayingAsync();
        viewModel.OpenAppearanceCommand.Execute(null);
        viewModel.Look.PickFontColor("#22c55e");
        var fallback = SubtitleAppearanceEditor.WebDefault with { FontColor = "#9ca3af" };
        _settings.Set(SettingKeys.SubtitleAppearance, Json(fallback), "default");

        await viewModel.ResetAppearanceCommand.ExecuteAsync(null);

        Assert.Equal("reset " + SettingKeys.SubtitleAppearance, _settings.Writes[^1]);
        Assert.Equal(fallback, viewModel.Look.Appearance);
        Assert.Equal(fallback, _engine.SubtitleLook);
    }

    [Fact]
    public async Task ANewTitle_ClosesThePanel()
    {
        var viewModel = await PlayingAsync();
        viewModel.OpenAppearanceCommand.Execute(null);

        await viewModel.StartAsync(PlayRequest.Resume("movie-2"));

        Assert.False(viewModel.IsAppearanceOpen);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5);
        }
        Assert.True(condition());
    }
}
