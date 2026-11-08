using Granary.Core.Api.Catalog;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.Settings;

/// <summary>Which episodes spoiler protection hides, as the web's lib/episodeSpoilers.ts and the apps decide it.</summary>
public sealed class EpisodeSpoilersTests
{
    private static readonly Uri Server = new("https://silo.test/media");
    private static readonly EpisodeSpoilers Both = new(HidesImages: true, HidesOverviews: true);

    private static Episode Episode(WatchRollup? watch = null, bool? still = null) => new()
    {
        ContentId = "episode-1",
        SeasonNumber = 1,
        EpisodeNumber = 2,
        Title = "Chapter 2",
        Overview = "Assane returns to Paris.",
        StillUrl = "/api/v2/artwork/episodes/2/w400.webp?sig=e",
        StillIsEpisodeStill = still,
        UserData = watch,
    };

    private static CatalogItem SectionEpisode(bool played = false, double? position = null, bool? backdropIsStill = true, bool? posterIsStill = null,
        string? backdrop = "/api/v2/artwork/episodes/2/backdrop.webp?sig=b") => new()
    {
        ContentId = "episode-1",
        Type = CatalogItemTypes.Episode,
        Title = "Chapter 2",
        SeriesTitle = "Lupin",
        SeasonNumber = 1,
        EpisodeNumber = 2,
        BackdropUrl = backdrop,
        BackdropIsEpisodeStill = backdropIsStill,
        PosterUrl = "/api/v2/artwork/series/1/poster.webp?sig=p",
        PosterIsEpisodeStill = posterIsStill,
        PositionSeconds = position,
        DurationSeconds = position is null ? null : 3000,
        ItemSource = position is null ? ItemSources.NextUp : ItemSources.InProgress,
        UserState = new CatalogItemUserState(played, false, false, false),
    };

    [Fact]
    public void AnEpisodeNeitherPlayedNorStarted_IsUnwatched()
    {
        Assert.True(EpisodeSpoilers.IsUnwatched(new WatchRollup()));
        Assert.True(EpisodeSpoilers.IsUnwatched(new WatchRollup { IsInProgress = false, PositionSeconds = 0 }));
        // No watch state at all: the episode is never revealed just because the payload left it out.
        Assert.True(EpisodeSpoilers.IsUnwatched(null));
    }

    [Fact]
    public void AnEpisodePlayedOrStarted_IsNotUnwatched()
    {
        Assert.False(EpisodeSpoilers.IsUnwatched(new WatchRollup { Played = true }));
        Assert.False(EpisodeSpoilers.IsUnwatched(new WatchRollup { IsInProgress = true }));
        Assert.False(EpisodeSpoilers.IsUnwatched(new WatchRollup { PositionSeconds = 12 }));
    }

    [Fact]
    public void OnlyAnUnwatchedEpisodesOwnStill_IsHidden()
    {
        Assert.True(Both.HidesImage(Episode()));
        Assert.True(Both.HidesImage(Episode(still: true)));
        Assert.False(Both.HidesImage(Episode(new WatchRollup { Played = true })));
        Assert.False(Both.HidesImage(Episode(new WatchRollup { PositionSeconds = 300 })));
        // The series' art standing in for a missing still gives nothing away.
        Assert.False(Both.HidesImage(Episode(still: false)));
    }

    [Fact]
    public void TheDescription_IsHiddenForEveryUnwatchedEpisode()
    {
        Assert.True(Both.HidesOverview(Episode(still: false)));
        Assert.False(Both.HidesOverview(Episode(new WatchRollup { IsInProgress = true })));
    }

    [Fact]
    public void Off_HidesNothing_AndEachSwitchHidesOnlyItsOwn()
    {
        Assert.False(EpisodeSpoilers.Off.HidesImage(Episode()));
        Assert.False(EpisodeSpoilers.Off.HidesOverview(Episode()));
        Assert.False(new EpisodeSpoilers(HidesImages: false, HidesOverviews: true).HidesImage(Episode()));
        Assert.False(new EpisodeSpoilers(HidesImages: true, HidesOverviews: false).HidesOverview(Episode()));
    }

    [Fact]
    public void ARowCard_IsHiddenByItsShownPicturesProvenance()
    {
        Assert.True(Both.HidesImage(SectionEpisode(), showsBackdrop: true));
        Assert.False(Both.HidesImage(SectionEpisode(backdropIsStill: false), showsBackdrop: true));
        // Without a backdrop the card shows the poster, and its provenance decides.
        Assert.False(Both.HidesImage(SectionEpisode(posterIsStill: false), showsBackdrop: false));
        Assert.True(Both.HidesImage(SectionEpisode(posterIsStill: null), showsBackdrop: false));
    }

    [Fact]
    public void ARowCard_OfAnEpisodeStartedOrPlayed_IsNotHidden()
    {
        Assert.False(Both.HidesImage(SectionEpisode(position: 600), showsBackdrop: true));
        Assert.False(Both.HidesImage(SectionEpisode(played: true), showsBackdrop: true));
    }

    [Fact]
    public void ARowCardsDescription_IsHiddenForAnUnwatchedEpisodeOnly()
    {
        // Whatever its picture: the description is the episode's own.
        Assert.True(Both.HidesOverview(SectionEpisode(backdropIsStill: false)));
        Assert.False(Both.HidesOverview(SectionEpisode(position: 600)));
        Assert.False(Both.HidesOverview(SectionEpisode(played: true)));
        Assert.False(Both.HidesOverview(SectionEpisode() with { Type = CatalogItemTypes.Movie }));
        Assert.False(new EpisodeSpoilers(HidesImages: true, HidesOverviews: false).HidesOverview(SectionEpisode()));
    }

    [Fact]
    public void Movies_AreNeverHidden()
    {
        var movie = SectionEpisode() with { Type = CatalogItemTypes.Movie };
        var series = SectionEpisode() with { Type = CatalogItemTypes.Series };

        Assert.False(Both.HidesImage(movie, showsBackdrop: true));
        Assert.False(Both.HidesImage(series, showsBackdrop: true));
        Assert.False(MediaCardFactory.Landscape(movie, Server, Both).BlursImage);
    }

    [Fact]
    public void ContinueWatchingAndNextUpCards_BlurAnUnwatchedEpisodesStill()
    {
        var section = new Section { SectionType = SectionTypes.NextUp };

        var card = MediaCardFactory.ForSection(section, SectionEpisode(), Server, Both);

        Assert.True(card.BlursImage);
        Assert.Equal("Lupin", card.Title);
        Assert.False(MediaCardFactory.ForSection(section, SectionEpisode(), Server).BlursImage);
        Assert.False(MediaCardFactory.ForSection(section, SectionEpisode(position: 600), Server, Both).BlursImage);
        // Poster rows keep every picture, as the web's other rows do.
        Assert.False(MediaCardFactory.ForSection(new Section { SectionType = SectionTypes.RecentlyAdded }, SectionEpisode(), Server, Both).BlursImage);
    }

    [Fact]
    public void AnEpisodeCard_BlursItsStill_AndLeavesItsDescriptionOut_ButKeepsItsTitle()
    {
        var card = MediaCardFactory.Episode(Episode(), Server, Both);

        Assert.True(card.BlursImage);
        Assert.Null(card.Caption);
        Assert.Equal("S01E02 · Chapter 2", card.Title);

        var shown = MediaCardFactory.Episode(Episode(), Server);
        Assert.False(shown.BlursImage);
        Assert.Equal("Assane returns to Paris.", shown.Caption);
    }

    [Fact]
    public void TheNextEpisode_KnowsWhetherItIsUnwatched_AndWhetherItsStillIsOne()
    {
        var unwatched = NextEpisode.Of(Episode(still: false), Server);
        var started = NextEpisode.Of(Episode(new WatchRollup { PositionSeconds = 30 }), Server);

        Assert.True(unwatched.IsUnwatched);
        Assert.False(unwatched.StillIsEpisodeStill);
        Assert.False(started.IsUnwatched);
        Assert.False(Both.HidesImage(unwatched.IsUnwatched, unwatched.StillIsEpisodeStill));
        Assert.True(Both.HidesOverview(unwatched.IsUnwatched));
    }
}
