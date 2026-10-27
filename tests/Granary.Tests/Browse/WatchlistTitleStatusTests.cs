using Granary.Core.Api.Requests;
using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

/// <summary>How a watchlist title the library lacks reads, as the web's lib/watchlistTitles.ts words it.</summary>
public sealed class WatchlistTitleStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static WatchlistTitle Title(RequestMediaState? request = null, string status = "active", string? releaseDate = null) => new()
    {
        TmdbId = 1,
        MediaType = "movie",
        Title = "Heat",
        Status = status,
        ReleaseDate = releaseDate,
        AddedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        Request = request ?? new RequestMediaState { Requestable = true },
    };

    private static WatchlistTitleStatus Of(WatchlistTitle title) => WatchlistTitleStatus.Of(title, Now, TimeZoneInfo.Utc);

    [Fact]
    public void ARequestableTitle_IsNotRequested_AndCanBe()
    {
        Assert.Equal(new WatchlistTitleStatus("Not requested", "Not requested", false, true, WatchlistTitleRank.NotRequested), Of(Title()));
    }

    [Fact]
    public void ARefusedRequest_SaysWhyInTheCaption()
    {
        var status = Of(Title(new RequestMediaState { Requestable = false, Reason = "quota_exceeded" }));

        Assert.Equal(("Not requested", "Not requested · request limit reached", false), (status.Badge, status.Caption, status.Requestable));
    }

    [Fact]
    public void ANotRequestedTitleStillToCome_SaysWhenItIsOut()
    {
        Assert.Equal("Not requested · out dec 18", Of(Title(releaseDate: "2026-12-18")).Caption);
    }

    [Fact]
    public void Today_IsTheViewersDay_AndATitleOutTodayIsOutAlready()
    {
        // 23:30 UTC on 30 September is already 1 October at UTC+2.
        var now = new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.Zero);
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

        Assert.Equal("Not requested", WatchlistTitleStatus.Of(Title(releaseDate: "2026-10-01"), now, plusTwo).Caption);
        Assert.Equal("Not requested · out oct 2", WatchlistTitleStatus.Of(Title(releaseDate: "2026-10-02"), now, plusTwo).Caption);
        Assert.Equal("Not requested · out oct 1", WatchlistTitleStatus.Of(Title(releaseDate: "2026-10-01"), now, TimeZoneInfo.Utc).Caption);
    }

    [Fact]
    public void APendingRequestForATitleOutLongAgo_AwaitsApproval()
    {
        var status = Of(Title(new RequestMediaState { Status = "pending" }, releaseDate: "2001-01-01"));

        Assert.Equal(("Awaiting approval", "Awaiting approval", WatchlistTitleRank.AwaitingApproval), (status.Badge, status.Caption, status.Rank));
    }

    [Fact]
    public void AQueuedRequest_IsProcessing()
    {
        var status = Of(Title(new RequestMediaState { Status = "queued" }));

        Assert.Equal(("Processing", WatchlistTitleRank.Downloading), (status.Badge, status.Rank));
    }

    [Fact]
    public void ADownloadWithoutItsSize_ShowsItsPhase()
    {
        var status = Of(Title(new RequestMediaState
        {
            Status = "downloading",
            Download = new RequestDownload { Phase = "queued", UpdatedAt = Now },
        }));

        Assert.Equal(("Waiting to download", "Waiting to download"), (status.Badge, status.Caption));
    }

    [Theory]
    [InlineData("needs_review", "Needs attention", "TMDB lists it twice")]
    [InlineData("removed", "Not on TMDB", "No longer listed on TMDB")]
    public void ATitleTmdbLost_NeedsAttention(string status, string badge, string caption)
    {
        var title = Title(status: status);

        Assert.Equal(new WatchlistTitleStatus(badge, caption, true, false, WatchlistTitleRank.Attention), Of(title));
        Assert.True(WatchlistTitleStatus.NeedsAttention(title));
    }

    [Fact]
    public void AStatusThisClientDoesNotKnow_ReadsAsActive()
    {
        var title = Title(status: "some_future_value");

        Assert.False(WatchlistTitleStatus.NeedsAttention(title));
        Assert.Equal("Not requested", Of(title).Badge);
    }

    [Fact]
    public void WithinAGroup_TheOneAddedLastComesFirst()
    {
        var older = Title() with { TmdbId = 1, AddedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) };
        var newer = Title() with { TmdbId = 2, AddedAt = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero) };

        Assert.Equal([2L, 1L], WatchlistTitleStatus.SoonestFirst([older, newer], Now, TimeZoneInfo.Utc).Select(t => t.TmdbId));
    }
}
