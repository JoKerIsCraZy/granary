using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The dot in the settings button's gear: green while the server sends the original streams, amber while it re-encodes
/// any of them, none without a server stream. It follows every plan the playback takes.
/// </summary>
public sealed class PlayerViewModelStreamMethodTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel Create() =>
        new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback();
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), new ManualTimeProvider());

    private static readonly WatchFileVersion File = new() { FileId = "42", Resolution = "2160p", CodecVideo = "hevc", CodecAudio = "truehd" };

    private static PlaybackStreamInfo Stream(string delivery, params string[] serverTransformations) => new(
        new PlaybackPlan
        {
            Delivery = delivery,
            Transformations = [.. serverTransformations.Select(name => new PlaybackTransformation { Name = name, Executor = "server" })],
        },
        new Uri("https://silo.example/api/v2/stream/s"), File, File, AudioOrdinal: 0);

    private static readonly PlaybackStreamInfo DirectPlay = Stream(PlaybackDeliveries.OriginalHttp);
    private static readonly PlaybackStreamInfo Remux = Stream(PlaybackDeliveries.ServerRemuxHls);
    private static readonly PlaybackStreamInfo AudioConverted = Stream(PlaybackDeliveries.ServerRemuxHls, "audio_to_aac");
    private static readonly PlaybackStreamInfo Transcode = Stream(PlaybackDeliveries.ServerTranscodeHls, "video_to_h264", "audio_to_aac");

    private async Task<PlayerViewModel> PlayingAsync(PlaybackStreamInfo stream)
    {
        var viewModel = Create();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.StreamInfo = stream;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        return viewModel;
    }

    // A pick that replans: the change starts, the server's new plan is taken, and its stream's first frame ends the change.
    private void Replans(PlanChange change, PlaybackStreamInfo stream)
    {
        Playback.RaisePlanChange(change);
        Playback.RaiseStream(stream);
        Playback.RaisePlanChange(PlanChange.None);
    }

    [Fact]
    public async Task NothingPlaysYet_TheGearShowsNoMethod()
    {
        var viewModel = Create();

        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));

        Assert.Equal(StreamMethod.None, viewModel.StreamMethod);
    }

    [Fact]
    public async Task TheFirstStream_ShowsItsMethod_OnceItPlays()
    {
        var viewModel = Create();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));

        // The playback takes the plan's tracks before it has the stream's address.
        Playback.RaiseTracks([], null);
        Assert.Equal(StreamMethod.None, viewModel.StreamMethod);

        Playback.StreamInfo = DirectPlay;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Assert.Equal(StreamMethod.DirectPlay, viewModel.StreamMethod);
    }

    [Theory]
    [InlineData(PlaybackDeliveries.OriginalHttp, StreamMethod.DirectPlay)]
    [InlineData(PlaybackDeliveries.ServerRemuxProgressive, StreamMethod.DirectStream)]
    [InlineData(PlaybackDeliveries.ServerRemuxHls, StreamMethod.DirectStream)]
    [InlineData(PlaybackDeliveries.ServerTranscodeHls, StreamMethod.Transcode)]
    public async Task TheMethod_IsThePlansThatPlays(string delivery, StreamMethod method)
    {
        var viewModel = await PlayingAsync(Stream(delivery));

        Assert.Equal(method, viewModel.StreamMethod);
    }

    [Fact]
    public async Task ALowerQuality_TurnsItToATranscode_AndAutoBackToDirectPlay()
    {
        var viewModel = await PlayingAsync(DirectPlay);

        Replans(PlanChange.Quality, Transcode);
        Assert.Equal(StreamMethod.Transcode, viewModel.StreamMethod);

        Replans(PlanChange.Quality, DirectPlay);
        Assert.Equal(StreamMethod.DirectPlay, viewModel.StreamMethod);
    }

    [Fact]
    public async Task AnotherVersion_ShowsItsOwnMethod()
    {
        var viewModel = await PlayingAsync(Transcode);

        Replans(PlanChange.Version, Remux);

        Assert.Equal(StreamMethod.DirectStream, viewModel.StreamMethod);
    }

    [Fact]
    public async Task AnAudioTrackTheServerConverts_IsATranscode_ThoughTheVideoIsCopied()
    {
        var viewModel = await PlayingAsync(Remux);

        Replans(PlanChange.Audio, AudioConverted);

        Assert.Equal(StreamMethod.Transcode, viewModel.StreamMethod);
    }

    [Fact]
    public async Task ARestart_ShowsTheMethodOfItsNewPlan()
    {
        var viewModel = await PlayingAsync(DirectPlay);

        Playback.RaisePlanChange(PlanChange.Restart);
        Playback.StreamInfo = Transcode;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));

        Assert.Equal(StreamMethod.Transcode, viewModel.StreamMethod);
    }

    [Fact]
    public async Task EachChange_IsReportedOnce()
    {
        var viewModel = await PlayingAsync(DirectPlay);
        var changes = new List<StreamMethod>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.StreamMethod))
            {
                changes.Add(viewModel.StreamMethod);
            }
        };

        Replans(PlanChange.Quality, Transcode);
        Replans(PlanChange.Quality, DirectPlay);

        Assert.Equal([StreamMethod.Transcode, StreamMethod.DirectPlay], changes);
    }

    [Fact]
    public async Task AFailedPlayback_ShowsNoMethod()
    {
        var viewModel = await PlayingAsync(DirectPlay);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "The server stopped responding.", CanRetry: true));

        Assert.Equal(StreamMethod.None, viewModel.StreamMethod);
    }

    [Fact]
    public async Task LeavingThePlayer_ShowsNoMethod()
    {
        var viewModel = await PlayingAsync(Transcode);

        viewModel.Leave();

        Assert.Equal(StreamMethod.None, viewModel.StreamMethod);
    }

    [Fact]
    public async Task AFilePlayedDirectly_ShowsNoMethod_EvenAfterAServerTitle()
    {
        var viewModel = await PlayingAsync(Transcode);

        await viewModel.StartAsync(PlayRequest.Direct(@"C:\Videos\clip.mkv"));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2), null, HasStarted: true));

        Assert.Equal(PlayerPhase.Playing, viewModel.Phase);
        Assert.Equal(StreamMethod.None, viewModel.StreamMethod);
    }
}
