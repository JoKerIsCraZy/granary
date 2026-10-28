using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>Notices from the server or its administrator, as the web player shows them: eight seconds, held while unseen.</summary>
public sealed class PlayerViewModelNoticeTests
{
    private static readonly PlaybackNotice Message = new("Playback notice", "Server restart in 5 minutes.");

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private async Task<PlayerViewModel> PlayingAsync()
    {
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback();
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        return viewModel;
    }

    [Fact]
    public async Task Notice_ShowsForEightSeconds()
    {
        var viewModel = await PlayingAsync();

        Playback.RaiseNotice(Message);

        Assert.True(viewModel.HasNotice);
        Assert.Equal(("Playback notice", "Server restart in 5 minutes.", false), (viewModel.NoticeTitle, viewModel.NoticeMessage, viewModel.IsNoticeWarning));
        _time.Advance(TimeSpan.FromSeconds(7.9));
        Assert.True(viewModel.HasNotice);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.False(viewModel.HasNotice);
    }

    [Fact]
    public async Task NewerNotice_TakesThePlace_AndKeepsItsOwnTime()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseNotice(Message);
        _time.Advance(TimeSpan.FromSeconds(6));

        Playback.RaiseNotice(new PlaybackNotice("Server restarting", "Playback may end shortly.", IsWarning: true));
        _time.Advance(TimeSpan.FromSeconds(6));

        Assert.True(viewModel.IsNoticeWarning);
        Assert.Equal("Server restarting", viewModel.NoticeTitle);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(viewModel.HasNotice);
    }

    [Fact]
    public async Task MinimizedPlayer_KeepsTheNotice_UntilItIsSeen()
    {
        var viewModel = await PlayingAsync();
        viewModel.HoldNotices(true);

        Playback.RaiseNotice(Message);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(viewModel.HasNotice);

        viewModel.HoldNotices(false);
        _time.Advance(TimeSpan.FromSeconds(8));
        Assert.False(viewModel.HasNotice);
    }

    [Fact]
    public async Task Dismiss_AndLeaving_ClearTheNotice()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseNotice(Message);

        viewModel.DismissNoticeCommand.Execute(null);
        Assert.False(viewModel.HasNotice);

        Playback.RaiseNotice(Message);
        var first = Playback;
        viewModel.Leave();

        Assert.False(viewModel.HasNotice);
        // The playback left behind says nothing any more.
        first.RaiseNotice(Message);
        Assert.False(viewModel.HasNotice);
    }
}
