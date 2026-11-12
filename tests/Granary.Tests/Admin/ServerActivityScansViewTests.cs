using System.Collections.Frozen;
using Granary.Core.Api.Admin;
using Granary.Core.Realtime;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Admin;

/// <summary>
/// The Scans section of the activity panel, as the web's <c>ScanRow</c>, <c>MoreRows</c> and <c>formatScanProgress</c>:
/// the rows' words, the 25 rows it lists, and the counts that are only a lower bound once the server's snapshot was full.
/// </summary>
public sealed class ServerActivityScansViewTests : IDisposable
{
    private static readonly FrozenSet<string> Everything = EventsChannels.Admin.ToFrozenSet();
    private static readonly FrozenDictionary<string, string> Names =
        new Dictionary<string, string> { ["1"] = "Movies", ["2"] = "Series" }.ToFrozenDictionary();

    private readonly ManualTimeProvider _clock = new();
    private readonly ServerActivity _activity;
    private readonly ServerActivityViewModel _indicator;

    public ServerActivityScansViewTests()
    {
        _activity = new ServerActivity(new AdminApi(new ScriptedSilo().Http), _clock, new LogSink().For<ServerActivity>());
        _indicator = new ServerActivityViewModel(_activity, new ImmediateDispatcher(), _clock);
    }

    public void Dispose()
    {
        _indicator.Dispose();
        _activity.Dispose();
    }

    private static ServerActivityState Live(params AdminScanRun[] scans) =>
        new() { Channels = Everything, Connection = EventsConnection.Live, Sessions = [], Tasks = [], Scans = scans, LibraryNames = Names };

    private static AdminScanRun Scan(string id, string status, long library = 1, string mode = "library", string? path = null,
        AdminScanResult? result = null) =>
        new() { Id = id, Status = status, LibraryId = library, Mode = mode, Path = path, Result = result };

    private static AdminScanRun[] Queued(int count) => [.. Enumerable.Range(0, count).Select(i => Scan("q" + i, "accepted"))];

    [Fact]
    public void AScansRow_SaysTheLibrary_ItsState_WhatItCovers_AndHowFarItIs()
    {
        _indicator.Show(Live(
            Scan("a", "running", library: 2, mode: "subtree", path: "/media/tv/Dark",
                result: new AdminScanResult { Message = "Matching files", TotalFiles = 4800, FilesProcessed = 1200 }),
            Scan("b", "accepted")));

        Assert.Equal(2, _indicator.ScanCount);
        Assert.Equal(2, _indicator.Total);
        var running = _indicator.Scans[0];
        Assert.Equal(("Series", true, "Scanning…", "Subtree · /media/tv/Dark", "Matching files · 1,200 / 4,800 (25%)"),
            (running.LibraryName, running.IsRunning, running.StatusText, running.DetailText, running.ProgressText));
        Assert.Equal("Series, Scanning…, Subtree · /media/tv/Dark, Matching files · 1,200 / 4,800 (25%)", running.ReadText);
        var queued = _indicator.Scans[1];
        Assert.Equal(("Movies", true, "Queued", "Full library", false), (queued.LibraryName, queued.IsQueued, queued.StatusText, queued.DetailText, queued.HasProgressText));
        Assert.Equal("Movies, Queued, Full library", queued.ReadText);
    }

    [Theory]
    [InlineData("library", null, "Full library")]
    [InlineData("subtree", "/media/tv", "Subtree · /media/tv")]
    [InlineData("file", "/media/a.mkv", "Single file · /media/a.mkv")]
    [InlineData("deep", null, "deep")]
    public void WhatAScanCovers_IsInTheWebsWords(string mode, string? path, string expected)
    {
        _indicator.Show(Live(Scan("a", "running", mode: mode, path: path)));

        Assert.Equal(expected, _indicator.Scans[0].DetailText);
    }

    [Fact]
    public void AScansProgress_IsWrittenAsTheWebWritesIt()
    {
        Assert.Equal("", ServerActivityText.ScanProgress(null));
        Assert.Equal("", ServerActivityText.ScanProgress(new AdminScanResult()));
        Assert.Equal("Discovering files", ServerActivityText.ScanProgress(new AdminScanResult { Message = "Discovering files", TotalFiles = 90 }));
        Assert.Equal("Processing files · 1 / 3 (33%)", ServerActivityText.ScanProgress(new AdminScanResult { TotalFiles = 3, FilesProcessed = 1 }));
        Assert.Equal("Saving · 12,345 / 12,345 (100%)",
            ServerActivityText.ScanProgress(new AdminScanResult { Message = "Saving", TotalFiles = 12345, FilesProcessed = 12345 }));
        // More processed than counted: never past 100.
        Assert.Equal("Saving · 15 / 10 (100%)", ServerActivityText.ScanProgress(new AdminScanResult { Message = "Saving", TotalFiles = 10, FilesProcessed = 15 }));
    }

    [Fact]
    public void ALibraryNobodyKnows_IsNamedByItsNumber()
    {
        _indicator.Show(Live(Scan("a", "running", library: 99)) with { LibraryNames = null });
        Assert.Equal("Library #99", _indicator.Scans[0].LibraryName);

        _indicator.Show(Live(Scan("a", "running", library: 99)));
        Assert.Equal("Library #99", _indicator.Scans[0].LibraryName);

        // Its name shows in the same row once it is read.
        var row = _indicator.Scans[0];
        _indicator.Show(Live(Scan("a", "running", library: 99)) with { LibraryNames = new Dictionary<string, string> { ["99"] = "Anime" }.ToFrozenDictionary() });
        Assert.Equal("Anime", row.LibraryName);
    }

    [Fact]
    public void OnlyQueuedAndRunningScans_ShowAndCount()
    {
        _indicator.Show(Live(Scan("a", "completed"), Scan("b", "failed"), Scan("c", "cancelled"), Scan("d", "accepted")));

        Assert.Equal(1, _indicator.ScanCount);
        Assert.Equal("d", Assert.Single(_indicator.Scans).Id);
    }

    [Fact]
    public void ThePanelListsTwentyFiveScans_AndCountsTheRest()
    {
        _indicator.Show(Live(Queued(25)));
        Assert.Equal(25, _indicator.Scans.Count);
        Assert.False(_indicator.HasMoreScans);

        _indicator.Show(Live(Queued(26)));
        Assert.Equal(25, _indicator.Scans.Count);
        Assert.Equal("1 more scan queued", _indicator.MoreScansText);

        _indicator.Show(Live(Queued(40)));
        Assert.Equal("15 more scans queued", _indicator.MoreScansText);
        Assert.Equal("40", _indicator.ScanCountText);
        Assert.Equal("40", _indicator.BadgeText);
        Assert.Equal("Server activity: 40 active", _indicator.Name);
    }

    [Fact]
    public void AFullSnapshot_MakesTheCountsALowerBound()
    {
        _indicator.Show(Live(Queued(ServerActivity.ScanSnapshotLimit)));

        Assert.True(_indicator.IsCountLowerBound);
        Assert.Equal("99+", _indicator.ScanCountText);
        Assert.Equal("99+", _indicator.BadgeText);
        Assert.Equal("Server activity: at least 500 active", _indicator.Name);
        Assert.Equal("At least 475 more scans queued", _indicator.MoreScansText);

        // Below the snapshot's size the number is exact again.
        _indicator.Show(Live(Queued(30)));
        Assert.False(_indicator.IsCountLowerBound);
        Assert.Equal("30", _indicator.BadgeText);
    }

    [Theory]
    [InlineData(12, false, "12")]
    [InlineData(12, true, "12+")]
    [InlineData(99, true, "99+")]
    [InlineData(100, false, "99+")]
    [InlineData(500, true, "99+")]
    public void ACount_IsWrittenAsTheWebWritesIt(int count, bool isLowerBound, string expected) =>
        Assert.Equal(expected, ServerActivityText.Count(count, isLowerBound));

    [Fact]
    public void WithoutScans_TheSectionSaysSo_ButOnlyWhatTheStreamSaid()
    {
        _indicator.Show(Live());
        Assert.True(_indicator.ShowsScans);
        Assert.True(_indicator.HasNoScans);
        Assert.Equal("No active scans", _indicator.ScanEmptyText);

        // Nobody knows without the stream: the line names the connection instead.
        _indicator.Show(Live() with { Connection = EventsConnection.Disconnected });
        Assert.Equal("Disconnected", _indicator.ScanEmptyText);
        _indicator.Show(Live() with { Connection = EventsConnection.Connecting });
        Assert.Equal("Connecting…", _indicator.ScanEmptyText);
        // Live, but the snapshot has not come yet.
        _indicator.Show(Live() with { Scans = null });
        Assert.Equal("Connecting…", _indicator.ScanEmptyText);
    }

    [Fact]
    public void ScansThatWereKnown_StayListed_WhileTheStreamIsAway()
    {
        _indicator.Show(Live(Scan("a", "running")) with { Connection = EventsConnection.Disconnected });

        Assert.Equal("a", Assert.Single(_indicator.Scans).Id);
        Assert.False(_indicator.HasNoScans);
    }

    [Fact]
    public void NothingGoingOn_IsSaidOnlyOnceTheScansAreKnown()
    {
        _indicator.Show(Live() with { Scans = null });
        Assert.False(_indicator.IsEmpty);
        Assert.True(_indicator.HasSections);

        _indicator.Show(Live());
        Assert.True(_indicator.IsEmpty);
        Assert.False(_indicator.HasSections);
    }

    [Fact]
    public void AServerWithoutTheScansChannel_HasNoScansSection_AndIsNotWaitedFor()
    {
        _indicator.Show(new ServerActivityState { Channels = new[] { EventsChannels.Sessions }.ToFrozenSet(), Connection = EventsConnection.Live, Sessions = [] });

        Assert.False(_indicator.ShowsScans);
        Assert.True(_indicator.IsEmpty);
    }
}
