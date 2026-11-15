using Granary.Core.Api.Catalog;
using Granary.Core.Texts;
using Granary.Core.ViewModels.WatchParty;
using Granary.Core.WatchParty;

namespace Granary.Tests.WatchParty;

public sealed class WatchPartyTextTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);

    private static readonly CatalogItem Episode = new()
    {
        ContentId = "episode-1",
        Type = CatalogItemTypes.Episode,
        Title = "Chapter 1",
        SeriesTitle = "Lupin",
        SeasonNumber = 3,
        EpisodeNumber = 1,
    };

    [Fact]
    public void AHeadline_NamesAnEpisodeOrAMovie_InTheWebsWords()
    {
        Assert.Equal("Lupin — S3 E1 \"Chapter 1\"", WatchPartyText.Headline(Episode));
        Assert.Equal("Lupin — S3 E1", WatchPartyText.Headline(Episode with { Title = "" }));
        Assert.Equal("Heat (1995)", WatchPartyText.Headline(new CatalogItem { Type = CatalogItemTypes.Movie, Title = "Heat", Year = 1995 }));
        Assert.Equal("Heat", WatchPartyText.Headline(new CatalogItem { Type = CatalogItemTypes.Movie, Title = "Heat" }));
    }

    [Fact]
    public void AHeadline_IsShownInTheLanguageOnScreen_ButStoredInEnglish()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal("Lupin – S3 F1 „Chapter 1“", WatchPartyText.Headline(Episode));
        Assert.Equal("Lupin — S3 E1 \"Chapter 1\"", WatchPartyText.StoredHeadline(Episode));
        // Storing one leaves the language on screen as it was.
        Assert.Equal("Lupin – S3 F1 „Chapter 1“", WatchPartyText.Headline(Episode));
    }

    [Theory]
    [InlineData(10, "now")]
    [InlineData(300, "5m")]
    [InlineData(7200, "2h")]
    public void AnActivitysTime_IsTheWebs(int seconds, string said) =>
        Assert.Equal(said, WatchPartyRoomViewModel.Ago(At, At.AddSeconds(seconds)));

    [Fact]
    public void AnActivitysTime_FollowsTheLanguageOnScreen()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal(("jetzt", "2 Std."), (WatchPartyRoomViewModel.Ago(At, At.AddSeconds(10)), WatchPartyRoomViewModel.Ago(At, At.AddHours(2))));
    }

    [Fact]
    public void ARecentRoomsWords_FollowItsStatus()
    {
        var row = new WatchPartyRecentRow(new WatchPartyRecentRoom("srv1", "7", "room-1", "ABCD1234", "proof", true, At));

        Assert.Equal(("Watch Party", "Your room"), (row.Title, row.Role));
        Assert.Equal((WatchPartyRecentStatus.Checking, "Checking…", "", false), (row.Status, row.StatusText, row.EndLabel, row.CanRejoin));
        row.Status = WatchPartyRecentStatus.Unknown;
        Assert.Equal(("Status unavailable", "", true), (row.StatusText, row.EndLabel, row.CanRejoin));
        row.Status = WatchPartyRecentStatus.Live;
        Assert.Equal(("Live", "", true, false), (row.StatusText, row.EndLabel, row.CanRejoin, row.IsDimmed));
        row.Status = WatchPartyRecentStatus.Ended;
        Assert.Equal(("Ended", "ENDED", false), (row.StatusText, row.EndLabel, row.CanRejoin));

        using var german = AppText.Use(TextLanguage.Load("de"));
        Assert.Equal(("Beendet", "BEENDET"), (row.StatusText, row.EndLabel));
    }

    [Fact]
    public void AHostsFailure_SaysWhatFailedAndWhy()
    {
        Assert.Equal("Could not start playback. The server isn't responding. Try again in a moment.",
            WatchPartyText.Failed("Could not start playback", "The server isn't responding. Try again in a moment."));
        Assert.Equal(("Invite copied. Room code ABCD1234", "Invite copied. Room code"), (WatchPartyText.InviteCopied("ABCD1234"), WatchPartyText.InviteCopied(null)));
    }
}
