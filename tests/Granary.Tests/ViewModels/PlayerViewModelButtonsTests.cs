using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The player's bar as Settings → Playback → Player buttons has it.</summary>
public sealed class PlayerViewModelButtonsTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryPlayerButtons _buttons = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel Create(IPlayerButtonsLayout? buttons) => new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
    {
        var playback = new FakeServerPlayback { DurationSeconds = 2700 };
        _playbacks.Add(playback);
        return playback;
    }, new PlaybackStops(), _time, buttons: buttons);

    private async Task<PlayerViewModel> PlayingAsync()
    {
        var viewModel = Create(_buttons);
        await viewModel.StartAsync(PlayRequest.Resume("episode-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        return viewModel;
    }

    [Fact]
    public void WithoutALayout_EveryButtonShows()
    {
        using var viewModel = Create(null);

        Assert.Equal(PlayerBar.Default, viewModel.Bar);
        Assert.True(viewModel.ShowsSleepTimer);
    }

    [Fact]
    public async Task ThePlayer_StartsWithTheProfilesBar_AndFollowsIt()
    {
        _buttons.Set(PlayerBar.Default.WithMoved(PlayerButton.FullScreen, -1).WithShown(PlayerButton.Chapters, false));
        var viewModel = await PlayingAsync();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(_buttons.Current, viewModel.Bar);
        _buttons.SetShown(PlayerButton.Chapters, true);

        Assert.True(viewModel.Bar.Chapters);
        Assert.Equal(PlayerButton.FullScreen, viewModel.Bar.Order[^2]);
        Assert.Contains(nameof(PlayerViewModel.Bar), changed);
    }
}
