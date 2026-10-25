using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The player page's Fill screen and Playback info.</summary>
public sealed class PlayerViewModelPictureTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private async Task<PlayerViewModel> PlayingAsync()
    {
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback { DurationSeconds = 2700 };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);
        await viewModel.StartAsync(PlayRequest.Resume("episode-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        return viewModel;
    }

    [Fact]
    public async Task FillScreen_FillsThePicture_UntilAnotherTitleStarts()
    {
        var viewModel = await PlayingAsync();

        viewModel.ToggleFillCommand.Execute(null);
        Assert.True(viewModel.IsFillingScreen);
        Assert.True(_engine.Fill);

        await viewModel.StartAsync(PlayRequest.Resume("episode-2"));
        Assert.False(viewModel.IsFillingScreen);
        Assert.False(_engine.Fill);
    }

    [Fact]
    public async Task PlaybackInfo_IsReadAgainEverySecond_WhileItShows()
    {
        var viewModel = await PlayingAsync();
        viewModel.SetPictureSize(1920, 1080);
        _engine.Stats = new PlaybackStats(1280, 720, 0, 0, "d3d11va");

        viewModel.TogglePlaybackInfoCommand.Execute(null);
        Assert.True(viewModel.IsPlaybackInfoShown);
        Assert.Equal("1920x1080", Row(viewModel, "Player dimensions"));
        Assert.Equal("0", Row(viewModel, "Dropped frames"));
        var shown = viewModel.PlaybackInfoSections;

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Same(shown, viewModel.PlaybackInfoSections);
        _engine.Stats = _engine.Stats with { DroppedFrames = 4 };
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("4", Row(viewModel, "Dropped frames"));

        viewModel.TogglePlaybackInfoCommand.Execute(null);
        _engine.Stats = _engine.Stats with { DroppedFrames = 9 };
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(viewModel.IsPlaybackInfoShown);
        Assert.Equal("4", Row(viewModel, "Dropped frames"));
    }

    private static string Row(PlayerViewModel viewModel, string label) =>
        viewModel.PlaybackInfoSections.SelectMany(s => s.Rows).First(r => r.Label == label).Value;
}
