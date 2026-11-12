using System.Collections.Frozen;
using Granary.Core.Api.Admin;
using Granary.Core.Realtime;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Admin;

/// <summary>
/// The Preparing downloads section of the activity panel, as the web's <c>PreparationsSummary</c> and
/// <c>adminDownloadPreparationPresentation.ts</c>: what counts, the three running jobs it lists, and the lines under them.
/// </summary>
public sealed class ServerActivityPreparationsViewTests : IDisposable
{
    private static readonly FrozenSet<string> Everything = EventsChannels.Admin.ToFrozenSet();

    private readonly ManualTimeProvider _clock = new();
    private readonly ServerActivity _activity;
    private readonly ServerActivityViewModel _indicator;

    public ServerActivityPreparationsViewTests()
    {
        _activity = new ServerActivity(new AdminApi(new ScriptedSilo().Http), _clock, new LogSink().For<ServerActivity>());
        _indicator = new ServerActivityViewModel(_activity, new ImmediateDispatcher(), _clock);
    }

    public void Dispose()
    {
        _indicator.Dispose();
        _activity.Dispose();
    }

    private static ServerActivityState Live(AdminDownloadPreparationCounts counts, params AdminDownloadPreparation[] jobs) => new()
    {
        Channels = Everything,
        Connection = EventsConnection.Live,
        Sessions = [],
        Tasks = [],
        Scans = [],
        Preparations = new AdminDownloadPreparationList { Counts = counts, Items = jobs },
    };

    private static AdminDownloadPreparationCounts Counts(int running = 0, int queued = 0, int retrying = 0, int paused = 0, int failed = 0) =>
        new() { Running = running, Queued = queued, Retrying = retrying, Paused = paused, FailedRecent = failed };

    private static AdminDownloadPreparation Episode(string id = "job-1", AdminDownloadPreparationProgress? progress = null) => new()
    {
        Id = id,
        State = "running",
        Format = "transcode",
        MediaFileId = "48211",
        MediaTitle = "Past and Present",
        SeriesName = "Dark",
        SeasonNumber = 1,
        EpisodeNumber = 3,
        Output = new AdminDownloadPreparationOutput("h264", "1080p"),
        Worker = new AdminDownloadPreparationWorker("node", "3", "gpu-1"),
        Progress = progress,
    };

    [Fact]
    public void RunningQueuedAndRetryingJobsCount_PausedAndFailedOnesDoNot()
    {
        _indicator.Show(Live(Counts(running: 1, queued: 2, retrying: 3, paused: 4, failed: 5), Episode()));

        Assert.True(_indicator.ShowsPreparations);
        Assert.Equal(6, _indicator.PreparationCount);
        Assert.Equal("6", _indicator.PreparationCountText);
        Assert.Equal(6, _indicator.Total);
        Assert.Equal("Server activity: 6 active", _indicator.Name);
    }

    [Fact]
    public void ARunningJob_ShowsItsTitle_ItsProgress_AndWhatIsMadeWhereAndHowLong()
    {
        _indicator.Show(Live(Counts(running: 1), Episode(progress: new AdminDownloadPreparationProgress(1275, 3000, 2))));

        var row = Assert.Single(_indicator.Preparations);
        Assert.Equal("Dark S01E03", row.Title);
        Assert.Equal((true, false, "42%", 42.5), (row.HasProgress, row.IsWorking, row.StatusText, row.ProgressValue));
        Assert.Equal("Transcode 1080p H.264 · gpu-1 · about 14 min left", row.DetailText);
        Assert.Equal("Dark S01E03 progress", row.ProgressName);
        Assert.Equal("Dark S01E03, 42%, Transcode 1080p H.264 · gpu-1 · about 14 min left", row.ReadText);
    }

    [Fact]
    public void WithoutAKnownProgress_ARunningJobSpins_AndSaysWhatItDoes()
    {
        _indicator.Show(Live(Counts(running: 2), Episode(), Episode("job-2") with { Format = "remux", Progress = new AdminDownloadPreparationProgress(10, 0, 0) }));

        Assert.Equal((true, "Encoding", "Transcode 1080p H.264 · gpu-1"), (_indicator.Preparations[0].IsWorking, _indicator.Preparations[0].StatusText,
            _indicator.Preparations[0].DetailText));
        Assert.Equal((true, "Remuxing", "Remux · gpu-1"), (_indicator.Preparations[1].IsWorking, _indicator.Preparations[1].StatusText,
            _indicator.Preparations[1].DetailText));
    }

    [Fact]
    public void AJobsTitle_IsTheEpisodesCode_TheMoviesTitle_OrTheFilesNumber()
    {
        Assert.Equal("Dark S01E03", ServerActivityText.PreparationTitle(Episode()));
        Assert.Equal("Dark S12E104", ServerActivityText.PreparationTitle(Episode() with { SeasonNumber = 12, EpisodeNumber = 104 }));
        Assert.Equal("Dune", ServerActivityText.PreparationTitle(new AdminDownloadPreparation { MediaTitle = "Dune", MediaFileId = "12" }));
        // A series without its numbers reads as its title.
        Assert.Equal("Past and Present", ServerActivityText.PreparationTitle(Episode() with { EpisodeNumber = null }));
        Assert.Equal("File #12", ServerActivityText.PreparationTitle(new AdminDownloadPreparation { MediaFileId = "12" }));
    }

    [Fact]
    public void WhatIsMade_IsInTheWebsWords()
    {
        Assert.Equal("Remux", ServerActivityText.PreparationKind(Episode() with { Format = "remux" }));
        Assert.Equal("Transcode 1080p H.264", ServerActivityText.PreparationKind(Episode()));
        Assert.Equal("Transcode HEVC", ServerActivityText.PreparationKind(Episode() with { Output = new AdminDownloadPreparationOutput("hevc", null) }));
        Assert.Equal("Transcode 720p VP9", ServerActivityText.PreparationKind(Episode() with { Output = new AdminDownloadPreparationOutput("vp9", "720p") }));
        // Only the audio is converted.
        Assert.Equal("Transcode", ServerActivityText.PreparationKind(Episode() with { Output = new AdminDownloadPreparationOutput("copy", "1080p") }));
        Assert.Equal("Transcode", ServerActivityText.PreparationKind(Episode() with { Output = null }));
    }

    [Theory]
    [InlineData("node", "3", "gpu-1", "gpu-1")]
    [InlineData("node", "3", "", "Node 3")]
    [InlineData("node", null, null, "Node")]
    [InlineData("server", null, "api-1", "api-1")]
    [InlineData("server", null, "", "Local server")]
    public void TheWorker_IsNamed_AsTheWebNamesIt(string kind, string? nodeId, string? name, string expected) =>
        Assert.Equal(expected, ServerActivityText.PreparationWorker(new AdminDownloadPreparationWorker(kind, nodeId, name)));

    [Fact]
    public void AJobWithoutAWorkerYet_NamesNone() => Assert.Equal("", ServerActivityText.PreparationWorker(null));

    [Theory]
    [InlineData(2941, 3000, 1, "under 1 min left")]
    [InlineData(2940, 3000, 1, "about 1 min left")]
    [InlineData(0, 3000, 2, "about 25 min left")]
    [InlineData(0, 7200, 2, "about 1 h left")]
    [InlineData(0, 7500, 1, "about 2 h 5 min left")]
    [InlineData(0, 3000, 0, "")]
    [InlineData(10, 0, 2, "")]
    public void TheTimeLeft_IsWrittenAsTheWebWritesIt(double encoded, double duration, double speed, string expected) =>
        Assert.Equal(expected, ServerActivityText.PreparationRemaining(new AdminDownloadPreparationProgress(encoded, duration, speed)));

    [Fact]
    public void TheProgress_StaysBetweenNothingAndAll_AndIsUnknownWithoutALength()
    {
        Assert.Null(ServerActivityText.PreparationPercent(null));
        Assert.Null(ServerActivityText.PreparationPercent(new AdminDownloadPreparationProgress(10, 0, 1)));
        Assert.Equal(100, ServerActivityText.PreparationPercent(new AdminDownloadPreparationProgress(4000, 3000, 1)));
        Assert.Equal(0, ServerActivityText.PreparationPercent(new AdminDownloadPreparationProgress(-5, 3000, 1)));
    }

    [Fact]
    public void ThreeRunningJobsAreListed_TheRestJoinTheWaitingLine()
    {
        _indicator.Show(Live(Counts(running: 5, queued: 4, retrying: 1),
            Episode("a"), Episode("b"), Episode("c"), Episode("d"), Episode("e"), Episode("q") with { State = "queued" }));

        Assert.Equal(["a", "b", "c"], _indicator.Preparations.Select(row => row.Id));
        Assert.Equal("2 more running · 4 queued · 1 waiting to retry", _indicator.WaitingPreparationsText);
        Assert.True(_indicator.HasWaitingPreparations);
        Assert.False(_indicator.HasNoPreparations);
    }

    [Fact]
    public void TheLinesUnderTheJobs_SayOnlyWhatThereIs()
    {
        _indicator.Show(Live(Counts(running: 1, queued: 1200), Episode()));
        Assert.Equal("1,200 queued", _indicator.WaitingPreparationsText);
        Assert.False(_indicator.HasPausedPreparations);
        Assert.False(_indicator.HasFailedPreparations);

        _indicator.Show(Live(Counts(retrying: 2, paused: 3, failed: 1)));
        Assert.Equal("2 waiting to retry", _indicator.WaitingPreparationsText);
        Assert.Equal("3 paused", _indicator.PausedPreparationsText);
        Assert.Equal("1 failed in the last 24 hours", _indicator.FailedPreparationsText);
    }

    [Fact]
    public void AnEmptyQueue_SaysSo()
    {
        _indicator.Show(Live(Counts()));

        Assert.True(_indicator.ShowsPreparations);
        Assert.True(_indicator.HasNoPreparations);
        Assert.False(_indicator.HasPreparations);
        Assert.True(_indicator.IsEmpty);
    }

    [Fact]
    public void ARecentFailure_IsNotHiddenBehindNothingGoingOn()
    {
        _indicator.Show(Live(Counts(failed: 2)));

        Assert.Equal(0, _indicator.Total);
        Assert.False(_indicator.IsEmpty);
        Assert.True(_indicator.HasSections);
        Assert.Equal("2 failed in the last 24 hours", _indicator.FailedPreparationsText);
        Assert.False(_indicator.HasNoPreparations);
        // Nothing counts, so on a regular page the button stays away, as on the web.
        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public void PausedJobsAlone_DoNotCount()
    {
        _indicator.Show(Live(Counts(paused: 2)));

        Assert.Equal(0, _indicator.Total);
        Assert.False(_indicator.IsVisible);
        Assert.Equal("2 paused", _indicator.PausedPreparationsText);
    }

    [Fact]
    public void AServerWithoutSuchAQueue_HasNoSection()
    {
        _indicator.Show(new ServerActivityState { Channels = Everything, Connection = EventsConnection.Live, Sessions = [], Scans = [] });

        Assert.False(_indicator.ShowsPreparations);
        Assert.False(_indicator.HasNoPreparations);
        Assert.Empty(_indicator.Preparations);
    }

    [Fact]
    public void AJobThatGoesOn_KeepsItsRow_WithItsNewProgress()
    {
        _indicator.Show(Live(Counts(running: 1), Episode(progress: new AdminDownloadPreparationProgress(300, 3000, 2))));
        var row = Assert.Single(_indicator.Preparations);

        _indicator.Show(Live(Counts(running: 1), Episode(progress: new AdminDownloadPreparationProgress(1500, 3000, 2))));

        Assert.Same(row, Assert.Single(_indicator.Preparations));
        Assert.Equal("50%", row.StatusText);
    }
}
