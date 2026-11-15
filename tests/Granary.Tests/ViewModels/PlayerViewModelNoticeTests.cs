using Granary.Core.Playback;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>Notices from the server or its administrator, as the web player shows them: eight seconds, held in the mini view.</summary>
public sealed class PlayerViewModelNoticeTests
{
    private static readonly PlaybackNotice Message = new("Playback notice", "Server restart in 5 minutes.");

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private Task<PlayerViewModel> PlayingAsync() => StartedAsync(PlayRequest.Resume("movie-1"));

    private async Task<PlayerViewModel> StartedAsync(PlayRequest request)
    {
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback();
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);
        await viewModel.StartAsync(request);
        return viewModel;
    }

    // The title's details are in, as the playback reports them.
    private void KnowTheTitle()
    {
        Playback.MediaInfo = new PlaybackMediaInfo("Example Movie", "2026", null);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
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
    public async Task MiniView_KeepsTheNotice_UntilItIsSeen()
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
    public async Task ATitleAPhoneStarted_SaysSo_ForFourSeconds_OnceItsTitleIsKnown()
    {
        var request = PlayRequest.At("movie-1", 4747.284);
        var viewModel = await StartedAsync(request);

        viewModel.NoteStartedFromPhone(request, "Elena's phone");
        // Its details are not in yet: there is no title to name.
        Assert.False(viewModel.HasNotice);
        KnowTheTitle();

        Assert.Equal(("Starting Example Movie", "From Elena's phone", false), (viewModel.NoticeTitle, viewModel.NoticeMessage, viewModel.IsNoticeWarning));
        _time.Advance(TimeSpan.FromSeconds(3.9));
        Assert.True(viewModel.HasNotice);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.False(viewModel.HasNotice);
        // Once is enough: the next status says nothing.
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Assert.False(viewModel.HasNotice);
    }

    [Fact]
    public async Task ATitleAPhoneStarted_WhoseTitleIsKnownAlready_SaysSoAtOnce()
    {
        var request = PlayRequest.Resume("movie-1");
        var viewModel = await StartedAsync(request);
        KnowTheTitle();

        viewModel.NoteStartedFromPhone(request, "Elena's phone");

        Assert.Equal("Starting Example Movie", viewModel.NoticeTitle);
    }

    [Fact]
    public async Task ATitleAPhoneStarted_SaysSo_InTheLanguageOnScreen()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        var request = PlayRequest.Resume("movie-1");
        var viewModel = await StartedAsync(request);
        KnowTheTitle();

        viewModel.NoteStartedFromPhone(request, "Elena's phone");

        Assert.Equal(("Example Movie wird gestartet", "Von Elena's phone"), (viewModel.NoticeTitle, viewModel.NoticeMessage));
    }

    [Fact]
    public async Task OnlyTheTitleThePhoneStarted_SaysSo()
    {
        // The window passes the phone's request on; another title (the next episode, a page's Play) is not the phone's.
        var viewModel = await StartedAsync(PlayRequest.Resume("movie-1"));

        viewModel.NoteStartedFromPhone(PlayRequest.Resume("movie-1"), "Elena's phone");
        KnowTheTitle();

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
