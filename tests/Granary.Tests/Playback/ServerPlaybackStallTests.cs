using Granary.Core.Playback;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>
/// A stream that stops delivering while the viewer plays, and the reconnect that brings it back playing or paused as the
/// viewer left it (docs/api-contract.md, 9.3; Android gives a stalled load 90 s, Apple's reconnect carries the play intent).
/// </summary>
public sealed class ServerPlaybackStallTests
{
    private readonly ServerPlaybackFixture _fixture = new();

    // Waiting for data at playerSeconds; heldTo: where what mpv holds from there ends, none when not past the position.
    private static PlaybackSnapshot Buffering(double playerSeconds, bool paused = false, double heldTo = 0) => Frame(playerSeconds, paused) with
    {
        IsBuffering = true,
        Buffered = heldTo > playerSeconds ? BufferedRanges.Of([new BufferedRange(playerSeconds - 10, heldTo)]) : BufferedRanges.None,
    };

    [Fact]
    public async Task BufferingWhileDataStillArrives_IsNoStall()
    {
        // A slow but live link (a remote server, a high bitrate) can take longer than the limit to fill mpv's cache: what
        // mpv holds still grows, a second of the title every 30 s here, so the stream is not lost.
        await _fixture.PlayingAsync();

        for (int step = 0; step < 6; step++)
        {
            _fixture.Engine.RaiseState(Buffering(700, heldTo: 700 + step));
            await _fixture.AdvanceAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(0, _fixture.Engine.Stops);
    }

    [Fact]
    public async Task AFlatBuffer_IsAStall_HoweverOftenMpvReportsIt()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Buffering(700, heldTo: 702));

        // Reports of what mpv holds that say nothing new (less than half a second more) keep the count going.
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(30));
        _fixture.Engine.RaiseState(Buffering(700, heldTo: 702.2));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(30));
        _fixture.Engine.RaiseState(Buffering(700, heldTo: 702.4));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(29));
        Assert.Equal(0, _fixture.Engine.Stops);
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, _fixture.Engine.Stops);
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Information ServerPlayback: Buffering at 700.0 s, 2.0 s held ahead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BufferingForAMinuteAndAHalf_WhileTheViewerPlays_Reconnects()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));

        _fixture.Engine.RaiseState(Buffering(700));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(89));
        Assert.Equal(0, _fixture.Engine.Stops);
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, _fixture.Engine.Stops);
        Assert.Equal(ServerPlaybackPhase.Reconnecting, playback.Status.Phase);
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;
        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("track_change", replan.GetProperty("operation").GetString());
        Assert.Equal(700, replan.GetProperty("position_seconds").GetDouble());
        Assert.Equal(TimeSpan.FromSeconds(700), _fixture.Engine.Loaded[^1].Start);
        Assert.False(_fixture.Engine.Current.IsPaused);
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Information ServerPlayback: Buffering at 700.0 s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BufferingWhileTheViewerPaused_IsLeftAlone()
    {
        // Nobody waits for the picture; the player reads on once it plays.
        var playback = await _fixture.PlayingAsync();

        _fixture.Engine.RaiseState(Buffering(700, paused: true));
        await _fixture.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(0, _fixture.Engine.Stops);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
    }

    [Fact]
    public async Task BufferingThatMovesOn_IsNoStall()
    {
        // A slow link buffers, plays a little and buffers again: it has not stalled.
        await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Buffering(700));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(60));
        _fixture.Engine.RaiseState(Frame(704));

        _fixture.Engine.RaiseState(Buffering(704));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, _fixture.Engine.Stops);
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Information ServerPlayback: Buffering ended at 704.0 s after 60.0 s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStreamThatRanOutOfData_ComesBackPlaying()
    {
        // A remux the server ended plays to the end of what mpv holds, where mpv pauses itself (keep-open). The viewer
        // paused nothing, so the stream the reconnect brings plays on.
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));

        _fixture.Engine.RaiseState(Frame(1500, paused: true));
        _fixture.Engine.RaiseState(Frame(1500, paused: true) with { IsAtEnd = true });
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;

        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.Equal(TimeSpan.FromSeconds(1500), _fixture.Engine.Loaded[^1].Start);
        Assert.False(_fixture.Engine.Current.IsPaused);
    }

    [Fact]
    public async Task AnEndLeftBehind_DoesNotUndoALaterPause()
    {
        // Held at the end of its data, then the viewer seeks back and pauses: a reload after that keeps the pause.
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(1500, paused: true) with { IsAtEnd = true });
        _fixture.Engine.RaiseState(Frame(300));
        _fixture.Engine.SetPaused(true);
        _fixture.Engine.RaiseState(Frame(300, paused: true));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await playback.Recovery;

        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.True(_fixture.Engine.Current.IsPaused);
    }

    [Fact]
    public async Task ARoomThatPausedWhileTheStreamWasDown_StaysPaused()
    {
        // In a Watch Party the room's pause reaches the engine while mpv holds the end of the data, already paused: the
        // stream the reconnect brings keeps the room's pause.
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.StartAsync(PlayRequest.WatchParty(MovieId, "42", selectionRevision: 1));
        _fixture.Engine.RaiseState(Frame(700));
        _fixture.Engine.RaiseState(Frame(1500, paused: true));
        _fixture.Engine.RaiseState(Frame(1500, paused: true) with { IsAtEnd = true });
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));

        _fixture.Engine.SetPaused(true);
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;

        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.True(_fixture.Engine.Current.IsPaused);
    }

    [Fact]
    public async Task AReconnect_KeepsTheViewersPause()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.SetPaused(true);
        _fixture.Engine.RaiseState(Frame(700, paused: true));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;

        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.True(_fixture.Engine.Current.IsPaused);
    }
}
