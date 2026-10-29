using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The sync indicator: one notice at a time about a sync, as Silo's web player shows it.</summary>
public sealed class SubtitleSyncFeedbackTests : IDisposable
{
    private const string Key = "stored-12";

    private readonly ManualTimeProvider _time = new();
    private readonly SubtitleSyncFeedback _feedback;

    public SubtitleSyncFeedbackTests() => _feedback = new SubtitleSyncFeedback(_time, new ImmediateDispatcher());

    public void Dispose() => _feedback.Dispose();

    private static SubtitleTiming Timing(long offsetMs) => new() { OffsetMs = offsetMs, RawScale = 1 };

    private static SubtitleSyncJob Job(string status, double? progress = null, string trigger = "manual", SubtitleTiming? result = null, string id = "j1") =>
        new() { Id = id, Status = status, Trigger = trigger, Progress = progress, Phase = status == "running" ? "analyzing" : null, Result = result };

    private static WatchedSubtitleSync Watched(SubtitleSyncJob job, SubtitleTiming? timing = null) => new(Key, "German", job, timing ?? SubtitleTiming.Identity);

    private static SubtitleSyncEntry Entry(SubtitleSyncJob? job = null, SubtitleTiming? timing = null, string? watched = null) =>
        new(new SubtitleSyncState { Key = Key, Timing = timing ?? SubtitleTiming.Identity, Sync = job }, WatchedJobId: watched);

    [Fact]
    public void ASyncTheViewerStarted_ShowsItsProgress()
    {
        _feedback.Update(Watched(Job("running", 0.4)), null, null);

        var notice = _feedback.Notice!;
        Assert.Equal(SubtitleSyncTone.Progress, notice.Tone);
        Assert.Equal("Syncing German subtitles", notice.Title);
        Assert.Equal("Listening to the audio…", notice.Detail);
        Assert.Equal(40, notice.Percent);
    }

    [Fact]
    public void ASyncOfTheSubtitleThatShows_SaysItIsApplied_OnceItsNewCuesShow()
    {
        _feedback.Update(Watched(Job("running", 0.9)), Key, Entry(Job("running", 0.9), watched: "j1"));
        var synced = Job("synced", result: Timing(-3000));

        _feedback.Update(Watched(synced, Timing(-3000)), Key, Entry(synced, Timing(-3000), "j1"));
        Assert.Equal("Applying new timing…", _feedback.Notice!.Title);
        Assert.True(_feedback.Notice.IsRunning);
        _feedback.TimingChanged(Key);
        Assert.Equal("Applying new timing…", _feedback.Notice!.Title);
        _feedback.CuesShown(Key);

        Assert.Equal(SubtitleSyncTone.Success, _feedback.Notice!.Tone);
        Assert.Equal("Subtitles synced", _feedback.Notice.Title);
        Assert.Equal("−3.0 s", _feedback.Notice.Detail);
    }

    [Fact]
    public void ASyncWhoseCuesNeverReload_IsStillCalledDone_AfterAWhile()
    {
        _feedback.Update(Watched(Job("running")), Key, Entry(Job("running"), watched: "j1"));
        var synced = Job("synced", result: Timing(500));
        _feedback.Update(Watched(synced, Timing(500)), Key, Entry(synced, Timing(500), "j1"));

        _time.Advance(SubtitleSyncFeedback.ApplyTimeout);

        Assert.Equal("Subtitles synced", _feedback.Notice!.Title);
    }

    [Theory]
    [InlineData("synced", SubtitleSyncTone.Success, "German subtitles synced")]
    [InlineData("already_synced", SubtitleSyncTone.Info, "German subtitles already match the audio")]
    [InlineData("no_match", SubtitleSyncTone.Warning, "German subtitles don't match the audio")]
    [InlineData("failed", SubtitleSyncTone.Warning, "Couldn't sync German subtitles")]
    public void ASyncOfAnotherSubtitle_SaysHowItEnded(string status, SubtitleSyncTone tone, string title)
    {
        _feedback.Update(Watched(Job("running")), "stored-99", null);

        _feedback.Update(Watched(Job(status, result: Timing(-500)), Timing(-500)), "stored-99", null);

        Assert.Equal(tone, _feedback.Notice!.Tone);
        Assert.Equal(title, _feedback.Notice.Title);
    }

    [Fact]
    public void AFinishedNotice_LeavesOnItsOwn_AWarningLater()
    {
        _feedback.Update(Watched(Job("already_synced")), null, null);
        _time.Advance(SubtitleSyncFeedback.NoticeVisible - TimeSpan.FromSeconds(1));
        Assert.NotNull(_feedback.Notice);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_feedback.Notice);

        _feedback.Update(Watched(Job("no_match", id: "j2")), null, null);
        _time.Advance(SubtitleSyncFeedback.NoticeVisible);
        Assert.NotNull(_feedback.Notice);
        _time.Advance(SubtitleSyncFeedback.WarningVisible - SubtitleSyncFeedback.NoticeVisible);
        Assert.Null(_feedback.Notice);
    }

    [Fact]
    public void AnOutcome_IsAnnouncedOnce()
    {
        _feedback.Update(Watched(Job("failed")), null, null);
        _feedback.Dismiss();

        _feedback.Update(Watched(Job("failed")), "stored-99", null);

        Assert.Null(_feedback.Notice);
    }

    [Fact]
    public void SomeoneElsesChangeToTheSubtitleThatShows_IsNoted_OnceItShows()
    {
        _feedback.Update(null, Key, Entry());

        _feedback.TimingChanged(Key);
        Assert.Null(_feedback.Notice);
        _feedback.CuesShown(Key);

        Assert.Equal(SubtitleSyncTone.Info, _feedback.Notice!.Tone);
        Assert.Equal("Subtitle timing updated", _feedback.Notice.Title);
    }

    [Fact]
    public void ASubtitleSyncedAutomatically_LinesUpWithoutAWord()
    {
        var automatic = Job("synced", trigger: "auto", result: Timing(1200));
        _feedback.Update(null, Key, Entry(automatic, Timing(1200)));

        _feedback.TimingChanged(Key);
        _feedback.CuesShown(Key);

        Assert.Null(_feedback.Notice);
    }

    [Fact]
    public void AnotherFile_LeavesNothingOfTheOldOne()
    {
        _feedback.Update(Watched(Job("running", 0.5)), Key, null);

        _feedback.Reset();

        Assert.Null(_feedback.Notice);
    }
}
