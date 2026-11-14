using System.Collections.Frozen;
using Granary.Core.Api.Admin;
using Granary.Core.Realtime;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Admin;

/// <summary>
/// The activity indicator, as the web's <c>components/ServerActivity.tsx</c>: the count on the button and its name, when
/// the button shows, the connection's state, and the panel's rows with the web's own words.
/// </summary>
public sealed class ServerActivityViewModelTests : IDisposable
{
    private static readonly FrozenSet<string> Everything = EventsChannels.Admin.ToFrozenSet();

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly LogSink _logs = new();
    private readonly ServerActivity _activity;
    private readonly ServerActivityViewModel _indicator;

    public ServerActivityViewModelTests()
    {
        _activity = new ServerActivity(new AdminApi(_silo.Http), _clock, _logs.For<ServerActivity>());
        _indicator = new ServerActivityViewModel(_activity, new ImmediateDispatcher(), _clock);
    }

    public void Dispose()
    {
        _indicator.Dispose();
        _activity.Dispose();
    }

    private static ServerActivityState Live(params AdminPlaybackSession[] sessions) =>
        new() { Channels = Everything, Connection = EventsConnection.Live, Sessions = sessions, Scans = [] };

    private static AdminPlaybackSession Session(string id, string method) => new() { SessionId = id, EffectivePlayMethod = method };

    [Fact]
    public void WithoutAnAdministratorsChannel_ThereIsNoButton()
    {
        _indicator.ShowsWhenIdle = true;

        _indicator.Show(new ServerActivityState { Connection = EventsConnection.Live });

        Assert.False(_indicator.IsAvailable);
        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public void OnARegularPage_TheButtonShowsOnlyWhileSomethingIsGoingOn()
    {
        _indicator.Show(Live());
        Assert.False(_indicator.IsVisible);

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        Assert.True(_indicator.IsVisible);

        _indicator.Show(Live());
        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public void InTheAdminArea_TheButtonShowsAlso_WhileNothingIsGoingOn()
    {
        _indicator.Show(Live());

        _indicator.ShowsWhenIdle = true;

        Assert.True(_indicator.IsVisible);
        Assert.False(_indicator.HasActivity);
        Assert.True(_indicator.IsEmpty);
        Assert.Equal("Server activity", _indicator.Name);
    }

    [Fact]
    public void TheButtonAndItsRows_AreWordedInTheLanguageOnScreen()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct), Session("s2", AdminPlayMethod.Direct)) with
        {
            Connection = EventsConnection.Disconnected,
        });

        Assert.Equal("Serveraktivität: 2 aktiv", _indicator.Name);
        Assert.Equal("2 Direktwiedergabe", Assert.Single(_indicator.Streams).Text);
        Assert.Equal("Getrennt", _indicator.ConnectionText);
    }

    [Fact]
    public void AnOpenPanel_KeepsItsButton_WhenTheLastActivityEnds()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        _indicator.IsPanelOpen = true;

        _indicator.Show(Live());
        Assert.True(_indicator.IsVisible);

        _indicator.IsPanelOpen = false;
        Assert.False(_indicator.IsVisible);
    }

    [Fact]
    public void TheCount_IsOnTheButton_AndInItsName()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct), Session("s2", AdminPlayMethod.Transcode)));

        Assert.Equal(2, _indicator.Total);
        Assert.Equal("2", _indicator.BadgeText);
        Assert.Equal("Server activity: 2 active", _indicator.Name);
        Assert.False(_indicator.IsEmpty);
    }

    [Theory]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(250, "99+")]
    public void Past99_TheCountSays99Plus(int streams, string expected)
    {
        _indicator.Show(Live([.. Enumerable.Range(0, streams).Select(i => Session("s" + i, AdminPlayMethod.Direct))]));

        Assert.Equal(expected, _indicator.BadgeText);
        Assert.Equal(expected, _indicator.StreamCountText);
        Assert.Equal($"Server activity: {streams} active", _indicator.Name);
    }

    [Fact]
    public void Streams_AreCountedPerPlayMethod_InTheWebsOrder()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Transcode), Session("s2", AdminPlayMethod.Direct), Session("s3", "something_new"),
            Session("s4", AdminPlayMethod.DirectStream), Session("s5", AdminPlayMethod.Direct), Session("s6", AdminPlayMethod.Remux),
            new AdminPlaybackSession { SessionId = "s7" }));

        Assert.True(_indicator.ShowsStreams);
        Assert.True(_indicator.HasStreams);
        Assert.Equal(7, _indicator.StreamCount);
        Assert.Equal(["2 Direct Play", "1 Remux", "1 Direct Stream", "1 Transcode", "1 Unknown", "1 Unknown"], _indicator.Streams.Select(row => row.Text));
        Assert.Equal(AdminPlayMethod.Direct, _indicator.Streams[0].Method);
    }

    [Fact]
    public void ARowThatStays_IsTheSameRow_WithItsNewCount()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Transcode), Session("s2", AdminPlayMethod.Direct)));
        var direct = _indicator.Streams[0];
        var transcode = _indicator.Streams[1];

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Transcode), Session("s3", AdminPlayMethod.Transcode), Session("s4", AdminPlayMethod.Remux)));

        Assert.Equal(["1 Remux", "2 Transcode"], _indicator.Streams.Select(row => row.Text));
        Assert.Same(transcode, _indicator.Streams[1]);
        Assert.DoesNotContain(direct, _indicator.Streams);
    }

    [Fact]
    public void AServerWithoutTheSessions_HasNoStreamsSection()
    {
        _indicator.Show(new ServerActivityState { Channels = Everything, Connection = EventsConnection.Live });

        Assert.False(_indicator.ShowsStreams);
        Assert.Empty(_indicator.Streams);
    }

    private static AdminTask Scheduled(string key, string name, string state, double progress = 0) =>
        new() { Key = key, Name = name, State = state, Progress = progress };

    [Fact]
    public void RunningTasks_AreListedAndCounted_TheOthersAreNot()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)) with
        {
            Tasks = [Scheduled("refresh_metadata", "Refresh Metadata", "running", 41.5), Scheduled("backup", "Back Up Server", "idle"),
                Scheduled("match_media", "Match Media Files", "running"), Scheduled("cleanup", "", "cancelling")],
        });

        Assert.True(_indicator.ShowsTasks);
        Assert.Equal(2, _indicator.TaskCount);
        Assert.Equal("2", _indicator.TaskCountText);
        Assert.Equal(3, _indicator.Total);
        Assert.Equal(["Refresh Metadata", "Match Media Files"], _indicator.Tasks.Select(row => row.Name));
        var known = _indicator.Tasks[0];
        Assert.Equal((true, false, "41.5%", 41.5), (known.HasProgress, known.IsWorking, known.StatusText, known.ProgressValue));
        Assert.Equal("Refresh Metadata, 41.5%", known.ReadText);
        var unknown = _indicator.Tasks[1];
        Assert.Equal((false, true, "Running"), (unknown.HasProgress, unknown.IsWorking, unknown.StatusText));
        Assert.Equal("Match Media Files, Running", unknown.ReadText);
    }

    [Fact]
    public void ATaskThatGoesOn_KeepsItsRow_WithItsNewProgress()
    {
        _indicator.Show(Live() with { Tasks = [Scheduled("refresh_metadata", "Refresh Metadata", "running", 10)] });
        var row = Assert.Single(_indicator.Tasks);

        _indicator.Show(Live() with { Tasks = [Scheduled("refresh_metadata", "Refresh Metadata", "running", 55)] });

        Assert.Same(row, Assert.Single(_indicator.Tasks));
        Assert.Equal("55%", row.StatusText);
    }

    [Fact]
    public void WithNothingRunning_TheSectionSaysSo_AndWithoutTheList_ThereIsNoSection()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)) with { Tasks = [Scheduled("backup", "Back Up Server", "idle")] });
        Assert.True(_indicator.HasNoTasks);
        Assert.Empty(_indicator.Tasks);

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        Assert.False(_indicator.ShowsTasks);
        Assert.False(_indicator.HasNoTasks);
    }

    [Fact]
    public void ARunningTaskAlone_ShowsTheButtonOnARegularPage()
    {
        _indicator.Show(Live() with { Tasks = [Scheduled("backup", "Back Up Server", "running")] });

        Assert.True(_indicator.IsVisible);
        Assert.Equal("Server activity: 1 active", _indicator.Name);
    }

    [Theory]
    [InlineData(0.4, "0.4%")]
    [InlineData(42, "42%")]
    [InlineData(12.34, "12.3%")]
    [InlineData(12.04, "12.0%")]
    [InlineData(41.97, "42.0%")]
    [InlineData(99.96, "99.9%")]
    [InlineData(100, "100%")]
    [InlineData(250, "100%")]
    [InlineData(double.NaN, "0%")]
    public void ATasksProgress_IsWrittenAsTheWebWritesIt(double progress, string expected) =>
        Assert.Equal(expected, ServerActivityText.TaskProgress(progress));

    [Fact]
    public void TheConnection_IsNamedInThePanel_AndOnTheButtonOnlyOnceItStayedAway()
    {
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        Assert.Equal("", _indicator.ConnectionText);

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)) with { Connection = EventsConnection.Disconnected });
        Assert.Equal("Disconnected", _indicator.ConnectionText);
        Assert.True(_indicator.HasConnectionText);
        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)) with { Connection = EventsConnection.Connecting });
        Assert.Equal("Connecting…", _indicator.ConnectionText);
        _clock.Advance(ServerActivityViewModel.ConnectionProblemDelay - TimeSpan.FromMilliseconds(1));
        Assert.False(_indicator.ShowsConnectionProblem);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_indicator.ShowsConnectionProblem);

        _indicator.Show(Live(Session("s1", AdminPlayMethod.Direct)));
        Assert.False(_indicator.ShowsConnectionProblem);
        Assert.False(_indicator.HasConnectionText);
    }

    [Fact]
    public void AConnectionBackInTime_NeverShowsTheProblem()
    {
        _indicator.Show(Live() with { Connection = EventsConnection.Disconnected });
        _clock.Advance(TimeSpan.FromSeconds(1));
        _indicator.Show(Live() with { Connection = EventsConnection.Connecting });
        _clock.Advance(TimeSpan.FromSeconds(1));

        _indicator.Show(Live());
        _clock.Advance(ServerActivityViewModel.ConnectionProblemDelay);

        Assert.False(_indicator.ShowsConnectionProblem);
    }

    [Fact]
    public void ViewAll_AsksTheShellForTheActivityPage()
    {
        int asked = 0;
        _indicator.StreamsRequested += (_, _) => asked++;

        _indicator.ViewStreamsCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    [Fact]
    public void TheActivitysNews_IsShown()
    {
        // What the stream brings reaches the indicator: a connection the server gave the scans, which need no read.
        long feed = _activity.Follow(ScriptedSilo.Base);
        _activity.Connection(feed, EventsConnection.Live);
        _activity.Handle(feed, EventsFrames.Parse("""{ "type": "subscribed", "request_id": "req-1", "channels": ["catalog", "scans"] }""")!);
        Assert.True(_indicator.IsAvailable);

        _activity.Follow(null);
        Assert.False(_indicator.IsAvailable);
    }

    private static EventsFrame ScanProgress(int processed) => EventsFrames.Parse(
        $$"""{ "type": "event", "channel": "scans", "event": "scan.progress", "event_id": "p", "timestamp": "t", "data": { "id": "a", "library_id": 1, "mode": "library", "trigger": "manual", "status": "running", "result": { "total_files": 4800, "files_processed": {{processed}} } } }""")!;

    // An indicator of its own on a dispatcher that counts, following a connection the server gave the scans.
    private (ServerActivityViewModel Indicator, LookCounter Looks, long Feed) Following()
    {
        var looks = new LookCounter();
        var indicator = new ServerActivityViewModel(_activity, looks, _clock);
        long feed = _activity.Follow(ScriptedSilo.Base);
        _activity.Connection(feed, EventsConnection.Live);
        _activity.Handle(feed, EventsFrames.Parse("""{ "type": "subscribed", "request_id": "req-1", "channels": ["scans"] }""")!);
        _activity.Handle(feed, EventsFrames.Parse("""{ "type": "snapshot", "channel": "scans", "timestamp": "t", "data": [] }""")!);
        // What is still to be shown of that, and a quiet moment after it.
        _clock.Advance(ServerActivityViewModel.ShowInterval);
        _clock.Advance(ServerActivityViewModel.ShowInterval);
        looks.Count = 0;
        return (indicator, looks, feed);
    }

    [Fact]
    public void NewsThatComesFasterThanFourTimesASecond_IsShownAtOnce_AndThenOncePerInterval()
    {
        var (indicator, looks, feed) = Following();
        using var _ = indicator;

        // A scan that counts its files tells of every step.
        for (int processed = 1; processed <= 100; processed++)
        {
            _activity.Handle(feed, ScanProgress(processed));
        }

        // The first step shows at once, the rest wait.
        Assert.Equal(1, looks.Count);
        Assert.Equal("Processing files · 1 / 4,800 (0%)", Assert.Single(indicator.Scans).ProgressText);
        _clock.Advance(ServerActivityViewModel.ShowInterval - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, looks.Count);

        // Then the latest of them, once.
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, looks.Count);
        Assert.Equal("Processing files · 100 / 4,800 (2%)", indicator.Scans[0].ProgressText);

        // More news within the next interval waits for its end; without any, nothing more is looked at.
        _activity.Handle(feed, ScanProgress(150));
        Assert.Equal(2, looks.Count);
        _clock.Advance(ServerActivityViewModel.ShowInterval);
        Assert.Equal(3, looks.Count);
        _clock.Advance(ServerActivityViewModel.ShowInterval);
        _clock.Advance(ServerActivityViewModel.ShowInterval);
        Assert.Equal(3, looks.Count);

        // After a quiet interval the next news shows at once again.
        _activity.Handle(feed, ScanProgress(200));
        Assert.Equal(4, looks.Count);
        Assert.Equal("Processing files · 200 / 4,800 (4%)", indicator.Scans[0].ProgressText);
    }

    [Fact]
    public void WhoMaySeeTheIndicator_IsNeverKeptWaiting()
    {
        var (indicator, looks, feed) = Following();
        using var _ = indicator;
        _activity.Handle(feed, ScanProgress(1));
        Assert.Equal(1, looks.Count);

        // Another profile, within the interval of the last look.
        _activity.Follow(null);

        Assert.False(indicator.IsAvailable);
        Assert.Empty(indicator.Scans);
    }

    [Fact]
    public void ALookTheDispatcherDropped_CostsThatLook_AndNoMore()
    {
        var (indicator, looks, feed) = Following();
        using var _ = indicator;
        looks.Drops = true;
        _activity.Handle(feed, ScanProgress(1));
        Assert.Empty(indicator.Scans);

        looks.Drops = false;
        _activity.Handle(feed, ScanProgress(2));
        _clock.Advance(ServerActivityViewModel.ShowInterval);

        Assert.Equal("Processing files · 2 / 4,800 (0%)", Assert.Single(indicator.Scans).ProgressText);
    }

    [Fact]
    public void ARunningTaskWithoutAName_GoesByItsKey()
    {
        _indicator.Show(Live() with { Tasks = [Scheduled("cleanup_orphans", "", "running", 12)] });

        var row = Assert.Single(_indicator.Tasks);
        Assert.Equal("cleanup_orphans", row.Name);
        Assert.Equal("cleanup_orphans, 12%", row.ReadText);

        // Its name shows in the same row once the server gives one.
        _indicator.Show(Live() with { Tasks = [Scheduled("cleanup_orphans", "Clean Up Orphans", "running", 12)] });
        Assert.Equal("Clean Up Orphans", row.Name);
    }

    [Fact]
    public void RowsThatChangePlaces_StayTheSameRows_InTheNewOrder()
    {
        _indicator.Show(Live() with { Tasks = [Scheduled("a", "A", "running"), Scheduled("b", "B", "running"), Scheduled("c", "C", "running")] });
        var (a, b, c) = (_indicator.Tasks[0], _indicator.Tasks[1], _indicator.Tasks[2]);

        _indicator.Show(Live() with { Tasks = [Scheduled("c", "C", "running"), Scheduled("d", "D", "running"), Scheduled("a", "A", "running", 30)] });

        Assert.Equal(["c", "d", "a"], _indicator.Tasks.Select(row => row.Key));
        Assert.Same(c, _indicator.Tasks[0]);
        Assert.Same(a, _indicator.Tasks[2]);
        Assert.Equal("30%", a.StatusText);
        Assert.DoesNotContain(b, _indicator.Tasks);
    }

    [Fact]
    public void ItemsThatShareAKey_GetARowEach_AndLeaveNoneBehind()
    {
        // The server names every task once; a list that does not must still come out as long as it is.
        _indicator.Show(Live() with { Tasks = [Scheduled("a", "First", "running"), Scheduled("a", "Second", "running"), Scheduled("b", "B", "running")] });
        Assert.Equal(["a", "a", "b"], _indicator.Tasks.Select(row => row.Key));
        Assert.Equal(3, _indicator.TaskCount);

        _indicator.Show(Live() with { Tasks = [Scheduled("b", "B", "running"), Scheduled("a", "Second", "running")] });
        Assert.Equal(["b", "a"], _indicator.Tasks.Select(row => row.Key));

        _indicator.Show(Live() with { Tasks = [] });
        Assert.Empty(_indicator.Tasks);
    }

    [Fact]
    public void OnAGermanWindows_TheNumbersStayAsTheWebWritesThemInEnglish()
    {
        var before = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture);
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.CurrentUICulture = new("de-DE");

            _indicator.Show(Live() with
            {
                Tasks = [Scheduled("refresh_metadata", "Refresh Metadata", "running", 41.5)],
                Scans = [.. Enumerable.Range(0, 1300).Select(index => new AdminScanRun
                {
                    Id = "s" + index,
                    Status = "running",
                    LibraryId = 1234,
                    Mode = "library",
                    Result = new AdminScanResult { TotalFiles = 4800, FilesProcessed = 1200 },
                })],
                Preparations = new AdminDownloadPreparationList
                {
                    Counts = new AdminDownloadPreparationCounts { Running = 1, Queued = 1200, Paused = 2500, FailedRecent = 1100 },
                    Items = [new AdminDownloadPreparation { Id = "job-1", State = "running", MediaTitle = "Dune", Progress = new AdminDownloadPreparationProgress(1275.5, 3000, 1.5) }],
                },
            });

            Assert.Equal("41.5%", _indicator.Tasks[0].StatusText);
            Assert.Equal(("Library #1234", "Processing files · 1,200 / 4,800 (25%)"), (_indicator.Scans[0].LibraryName, _indicator.Scans[0].ProgressText));
            Assert.Equal("1,275 more scans queued", _indicator.MoreScansText);
            Assert.Equal(("42%", "Transcode · about 19 min left"), (_indicator.Preparations[0].StatusText, _indicator.Preparations[0].DetailText));
            Assert.Equal(("1,200 queued", "2,500 paused", "1,100 failed in the last 24 hours"),
                (_indicator.WaitingPreparationsText, _indicator.PausedPreparationsText, _indicator.FailedPreparationsText));
            Assert.Equal("Server activity: 2502 active", _indicator.Name);
        }
        finally
        {
            (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture) = before;
        }
    }

    /// <summary>Runs what is posted at once and counts it, or drops it as a dispatcher that is shutting down does.</summary>
    private sealed class LookCounter : Granary.Core.Abstractions.IUiDispatcher
    {
        public int Count { get; set; }

        public bool Drops { get; set; }

        public bool HasThreadAccess => true;

        public void Post(Action action)
        {
            if (Drops)
            {
                return;
            }
            Count++;
            action();
        }
    }
}
