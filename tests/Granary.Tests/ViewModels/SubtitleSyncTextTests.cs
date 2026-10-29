using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>How a subtitle's timing and sync read in the player, in Silo's web player's words.</summary>
public sealed class SubtitleSyncTextTests
{
    private static SubtitleTiming Timing(long offsetMs, double scale = 1) => new() { OffsetMs = offsetMs, RawScale = scale };

    private static SubtitleSyncJob Job(string status, double? progress = null, string? phase = null, SubtitleTiming? result = null,
        string? failure = null) => new() { Id = "j1", Status = status, Trigger = "manual", Progress = progress, Phase = phase, Result = result, Failure = failure };

    private static SubtitleSyncState State(SubtitleSyncJob? job = null, SubtitleTiming? timing = null, string source = "downloaded") =>
        new() { Key = "stored-12", Source = source, Timing = timing ?? SubtitleTiming.Identity, Sync = job };

    [Theory]
    [InlineData(2300, "+2.3 s")]
    [InlineData(-400, "−0.4 s")]
    [InlineData(3000, "+3.0 s")]
    [InlineData(0, "+0.0 s")]
    public void AnOffset_ReadsInSecondsWithItsSign(long offsetMs, string text)
    {
        Assert.Equal(text, SubtitleSyncText.Offset(offsetMs));
    }

    [Theory]
    [InlineData(25 / 23.976, "25→23.976 fps")]
    [InlineData(23.976 / 25, "23.976→25 fps")]
    [InlineData(1.012345, "×1.0123 speed")]
    [InlineData(1.0, null)]
    public void AStretch_ReadsAsTheFrameRateConversionItMostLikelyIs(double scale, string? text)
    {
        Assert.Equal(text, SubtitleSyncText.Scale(scale));
    }

    [Fact]
    public void ACorrection_NamesItsShiftAndItsStretch()
    {
        Assert.Equal("+2.3 s · 25→23.976 fps", SubtitleSyncText.Timing(Timing(2300, 25 / 23.976)));
        Assert.Equal("25→23.976 fps", SubtitleSyncText.Timing(Timing(0, 25 / 23.976)));
        Assert.Equal("−1.5 s", SubtitleSyncText.Timing(Timing(-1500)));
    }

    [Fact]
    public void ARunningJob_SaysWhatItDoes_AndHowFarItCame()
    {
        Assert.Equal("Listening to the audio…", SubtitleSyncText.Phase(Job("running", 0.42, "analyzing")));
        Assert.Equal("Matching lines to speech…", SubtitleSyncText.Phase(Job("running", 0.8, "matching")));
        Assert.Equal("Waiting to start…", SubtitleSyncText.Phase(Job("pending")));
        Assert.Equal(42, SubtitleSyncText.Percent(Job("running", 0.42)));
        Assert.Null(SubtitleSyncText.Percent(Job("synced")));
        Assert.Null(SubtitleSyncText.Phase(Job("failed")));
    }

    [Theory]
    [InlineData("subtitle_changed", "The subtitle changed while it was syncing. Try again.")]
    [InlineData("no_audio", "This video has no audio Silo can read.")]
    [InlineData("unavailable", "The server is busy. Try again in a few minutes.")]
    [InlineData("error", "Sync failed. Try again.")]
    [InlineData(null, "Sync failed. Try again.")]
    public void AFailure_IsToldInPlainWords(string? failure, string text)
    {
        Assert.Equal(text, SubtitleSyncText.Failure(failure));
    }

    [Fact]
    public void ATracksLine_SaysWhereItsSyncStands()
    {
        Assert.Null(SubtitleSyncText.Status(State()));
        Assert.Equal("Syncing…", SubtitleSyncText.Status(State(Job("pending"))));
        Assert.Equal("Syncing… 40%", SubtitleSyncText.Status(State(Job("running", 0.4))));
        Assert.Equal("Doesn't match this video", SubtitleSyncText.Status(State(Job("no_match"))));
        Assert.Equal("Sync failed", SubtitleSyncText.Status(State(Job("failed"))));
        Assert.Equal("Already in sync", SubtitleSyncText.Status(State(Job("already_synced"))));
        Assert.Equal("Original timing", SubtitleSyncText.Status(State(Job("synced", result: Timing(-3000)))));
        Assert.Equal("Synced −3.0 s", SubtitleSyncText.Status(State(Job("synced", result: Timing(-3000)), Timing(-3000))));
        Assert.Equal("Timing adjusted +1.0 s", SubtitleSyncText.Status(State(Job("synced", result: Timing(-3000)), Timing(1000))));
        Assert.Equal("Timing adjusted +1.0 s", SubtitleSyncText.Status(State(timing: Timing(1000))));
    }

    [Fact]
    public void TheTimingSection_ShowsNothing_WhereSyncIsNotOffered()
    {
        var entry = new SubtitleSyncEntry(State());

        Assert.False(SubtitleTimingPanel.For(entry, isAvailable: false, canSync: true).IsVisible);
        Assert.False(SubtitleTimingPanel.For(null, isAvailable: true, canSync: true).IsVisible);
        Assert.False(SubtitleTimingPanel.For(entry, isAvailable: true, canSync: false).IsVisible);
    }

    [Theory]
    [InlineData("downloaded", "Matches the timing to the audio for everyone watching.")]
    [InlineData("external", "Matches the timing to the audio for everyone. The file itself isn't changed.")]
    public void BeforeAnySync_TheSectionSaysWhatSyncDoes(string source, string hint)
    {
        var panel = SubtitleTimingPanel.For(new SubtitleSyncEntry(State(source: source)), isAvailable: true, canSync: true);

        Assert.True(panel.IsVisible);
        Assert.True(panel.ShowsSync);
        Assert.Equal("Sync to audio", panel.SyncLabel);
        Assert.False(panel.ShowsReset);
        Assert.True(panel.AreActionsEnabled);
        Assert.Equal(hint, panel.HintText);
    }

    [Fact]
    public void ARunningSync_ShowsItsBar_AndHoldsTheButtons()
    {
        var panel = SubtitleTimingPanel.For(new SubtitleSyncEntry(State(Job("running", 0.35, "analyzing"), Timing(500))), isAvailable: true, canSync: true);

        Assert.True(panel.IsRunning);
        Assert.Equal("Syncing…", panel.SyncLabel);
        Assert.Equal(35, panel.Percent);
        Assert.Equal("35%", panel.PercentText);
        Assert.Equal("Listening to the audio…", panel.PhaseText);
        Assert.False(panel.AreActionsEnabled);
        Assert.True(panel.ShowsReset);
        Assert.False(panel.HasHint);
        Assert.False(panel.HasResult);
    }

    [Fact]
    public void AFinishedSync_TellsItsResult()
    {
        var synced = SubtitleTimingPanel.For(new SubtitleSyncEntry(State(Job("synced", result: Timing(-3000)), Timing(-3000))), true, true);
        var noMatch = SubtitleTimingPanel.For(new SubtitleSyncEntry(State(Job("no_match"))), true, true);
        var failed = SubtitleTimingPanel.For(new SubtitleSyncEntry(State(Job("failed", failure: "no_audio"))), true, true);

        Assert.Equal("Synced to the audio: −3.0 s", synced.ResultText);
        Assert.False(synced.IsResultWarning);
        Assert.Equal("Doesn't match this video's audio; probably for another release.", noMatch.ResultText);
        Assert.True(noMatch.IsResultWarning);
        Assert.Equal("This video has no audio Silo can read.", failed.ResultText);
        Assert.False(synced.HasHint);
    }

    [Fact]
    public void ARefusal_ReplacesTheButtons_WithWhy()
    {
        var forbidden = SubtitleTimingPanel.For(new SubtitleSyncEntry(State(), IsForbidden: true), true, true);
        var busy = SubtitleTimingPanel.For(new SubtitleSyncEntry(State(), IsBusy: true, Error: "Sync failed"), true, true);

        Assert.True(forbidden.IsVisible);
        Assert.False(forbidden.ShowsActions);
        Assert.True(forbidden.IsForbidden);
        Assert.False(forbidden.HasHint);
        Assert.Equal("Sync failed", busy.ErrorText);
        Assert.False(busy.AreActionsEnabled);
        Assert.False(busy.HasResult);
    }
}
