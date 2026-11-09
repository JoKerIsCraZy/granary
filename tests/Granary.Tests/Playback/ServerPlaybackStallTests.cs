using Granary.Core.Playback;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>
/// A stream that stops delivering while the viewer plays (docs/api-contract.md, 9.3; Android gives a stalled load 90 s).
/// </summary>
public sealed class ServerPlaybackStallTests
{
    private readonly ServerPlaybackFixture _fixture = new();

    private static PlaybackSnapshot Buffering(double playerSeconds, bool paused = false) => Frame(playerSeconds, paused) with { IsBuffering = true };

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
}
