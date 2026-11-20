using System.Net;
using System.Text.Json;
using Granary.Core.Api.Catalog;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Which episodes a season's or a series' download dialog starts: "Only unwatched episodes" leaves out the ones the viewer
/// watched, and keeps the one in progress; a series' One time chooses, as the Apple app's series sheet, between the episode
/// its page plays next, that episode's season and every episode.
/// </summary>
public sealed class DownloadEpisodeChoiceTests : IAsyncDisposable
{
    private const string CreatePath = "api/v2/downloads";
    private const long GB = 1L << 30;

    private static readonly WatchRollup Watched = new() { Played = true };
    private static readonly WatchRollup InProgress = new() { IsInProgress = true, PositionSeconds = 600 };

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private static Episode Episode(int season, int number, WatchRollup? watch = null, long size = GB) => new()
    {
        ContentId = $"ep_{season}_{number}",
        SeasonNumber = season,
        EpisodeNumber = number,
        Title = $"Chapter {number}",
        Runtime = 50,
        UserData = watch,
        Files = [new EpisodeFile { FileId = $"f_{season}_{number}", Resolution = "1080p", FileSize = size }],
    };

    private static SeasonDownloadOffer SeasonOffer(params Episode[] episodes) =>
        DownloadOffers.ForSeason(new CatalogItemDetail { SeriesId = "sr_1", SeasonNumber = 1, Title = "Season 1", SeriesTitle = "Example Series" },
            episodes, _ => false)!;

    private async Task<DownloadOptionsViewModel> OptionsAsync()
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(true, true)));
        var feature = _h.NewFeature();
        await feature.RefreshAsync(Token);
        return new DownloadOptionsViewModel(_h.NewManager(feature: feature), feature, _h.Preferences, _h.Disk, new ImmediateDispatcher(), _h.Time,
            _h.Logs.For<DownloadOptionsViewModel>());
    }

    private async Task<DownloadOptionsViewModel> SeasonDialogAsync(params Episode[] episodes)
    {
        var options = await OptionsAsync();
        options.ForSeason(SeasonOffer(episodes));
        await options.SpaceRead;
        return options;
    }

    // The specials, then two seasons: the first watched, the second half-way; the series' page plays the second's third next.
    private static IReadOnlyList<(Season, IReadOnlyList<Episode>)> HalfWatched =>
    [
        (new Season { SeasonNumber = 0, Title = "Specials", IsSpecials = true }, [Episode(0, 1)]),
        (new Season { SeasonNumber = 1, Title = "Season 1" }, [Episode(1, 1, Watched), Episode(1, 2, Watched)]),
        (new Season { SeasonNumber = 2, Title = "Season 2" }, [Episode(2, 1, Watched), Episode(2, 2, InProgress), Episode(2, 3, size: 2 * GB)]),
    ];

    private static readonly CatalogItemDetail PlaysThird = new() { Title = "Example Series", PlayContentId = "ep_2_3", PlaySeasonNumber = 2 };

    // The series' dialog as a card or its page opens it, over what this PC has.
    private async Task<DownloadOptionsViewModel> SeriesDialogAsync(IReadOnlyList<(Season, IReadOnlyList<Episode>)> seasons, CatalogItemDetail? series = null)
    {
        var offer = DownloadOffers.ForSeries(series ?? new CatalogItemDetail { Title = "Example Series" }, "sr_1", seasons,
            id => _h.Store.FindByItem(Scope7, id) is not null)!;
        var options = await OptionsAsync();
        options.ForReading(_ => Task.FromResult<DownloadOffer>(offer));
        await options.Reading;
        await options.SpaceRead;
        return options;
    }

    // The server makes each of these episodes' downloads, in this order.
    private void Creates(params string[] episodes)
    {
        _h.List();
        _h.Silo.On(HttpMethod.Post, CreatePath, [.. episodes.Select(episode =>
            ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_" + episode, "ready", episodeId: episode))))]);
    }

    private List<string?> CreatedEpisodes() =>
        [.. _h.Silo.SentTo(HttpMethod.Post, CreatePath).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("episode_id").GetString())];

    // The tester at episode 7 of a season, half-way through it: 7 to the end.
    [Fact]
    public async Task ASeasonWithWatchedEpisodes_OffersOnlyUnwatched_On_AndCountsAndStartsTheRest()
    {
        Creates("ep_1_7", "ep_1_8");
        var options = await SeasonDialogAsync(Episode(1, 6, Watched), Episode(1, 7, InProgress, 2 * GB), Episode(1, 8, size: 3 * GB));

        Assert.True(options.ShowsOnlyUnwatched);
        Assert.True(options.OnlyUnwatched);
        Assert.Equal("Only unwatched episodes", DownloadOptionsViewModel.OnlyUnwatchedLabel);
        Assert.Equal("2 episodes · 5.0 GB", options.SizeText);

        Assert.True(await options.StartAsync());

        Assert.Equal(["ep_1_7", "ep_1_8"], CreatedEpisodes());
    }

    [Fact]
    public async Task OnlyUnwatchedTurnedOff_CountsAndStartsEveryEpisodeLeft()
    {
        Creates("ep_1_6", "ep_1_7", "ep_1_8");
        var options = await SeasonDialogAsync(Episode(1, 6, Watched), Episode(1, 7, InProgress, 2 * GB), Episode(1, 8, size: 3 * GB));

        options.OnlyUnwatched = false;

        Assert.Equal("3 episodes · 6.0 GB", options.SizeText);
        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_1_6", "ep_1_7", "ep_1_8"], CreatedEpisodes());
    }

    [Fact]
    public async Task ASeasonWithoutWatchedEpisodes_HasNoSwitch_AndStartsEveryEpisodeLeft()
    {
        Creates("ep_1_1", "ep_1_2");
        var options = await SeasonDialogAsync(Episode(1, 1, InProgress), Episode(1, 2));

        Assert.False(options.ShowsOnlyUnwatched);
        Assert.Equal("2 episodes · 2.0 GB", options.SizeText);
        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_1_1", "ep_1_2"], CreatedEpisodes());
    }

    // A season watched to the end opens with the switch off, never on "0 episodes" with Download off; turned on, it starts nothing.
    [Fact]
    public async Task ASeasonWhoseEpisodesLeftAreAllWatched_OpensWithOnlyUnwatchedOff()
    {
        Creates("ep_1_1", "ep_1_2");
        var options = await SeasonDialogAsync(Episode(1, 1, Watched), Episode(1, 2, Watched));

        Assert.Equal((true, false), (options.ShowsOnlyUnwatched, options.OnlyUnwatched));
        Assert.Equal(("2 episodes · 2.0 GB", true), (options.SizeText, options.CanStart));

        options.OnlyUnwatched = true;

        Assert.Equal(("0 episodes", false), (options.SizeText, options.CanStart));
        Assert.False(await options.StartAsync());
        Assert.Empty(CreatedEpisodes());

        options.OnlyUnwatched = false;

        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_1_1", "ep_1_2"], CreatedEpisodes());
    }

    // Each row the viewer picks starts the switch as a dialog would open on it, until the viewer sets it: then it stays.
    [Fact]
    public async Task ARowWhoseEpisodesAreAllWatched_StartsWithOnlyUnwatchedOff_UntilTheViewerSetsIt()
    {
        var options = await SeriesDialogAsync(
        [
            (new Season { SeasonNumber = 1, Title = "Season 1" }, [Episode(1, 1, Watched), Episode(1, 2, Watched)]),
            (new Season { SeasonNumber = 2, Title = "Season 2" }, [Episode(2, 1)]),
        ], new CatalogItemDetail { Title = "Example Series", PlayContentId = "ep_1_2", PlaySeasonNumber = 1 });

        options.SelectedOneTimeRow = Row(options, OneTimeChoice.Season);
        Assert.Equal((false, "2 episodes · 2.0 GB"), (options.OnlyUnwatched, options.SizeText));
        options.SelectedOneTimeRow = Row(options, OneTimeChoice.AllEpisodes);
        Assert.Equal((true, "1 episode · 1.0 GB"), (options.OnlyUnwatched, options.SizeText));

        options.OnlyUnwatched = false;
        options.SelectedOneTimeRow = Row(options, OneTimeChoice.Season);
        options.SelectedOneTimeRow = Row(options, OneTimeChoice.AllEpisodes);

        Assert.Equal((false, "3 episodes · 3.0 GB"), (options.OnlyUnwatched, options.SizeText));
    }

    [Fact]
    public async Task ASeries_OnlyUnwatched_LeavesOutItsWatchedEpisodes()
    {
        Creates("ep_2_1", "ep_2_2");
        var options = await SeriesDialogAsync(
        [
            (new Season { SeasonNumber = 1 }, [Episode(1, 1, Watched), Episode(1, 2, Watched)]),
            (new Season { SeasonNumber = 2 }, [Episode(2, 1, InProgress), Episode(2, 2)]),
        ]);

        options.SelectedOneTimeRow = Row(options, OneTimeChoice.AllEpisodes);

        Assert.True(options.ShowsOnlyUnwatched);
        Assert.Equal("2 episodes · 2.0 GB", options.SizeText);
        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_2_1", "ep_2_2"], CreatedEpisodes());
    }

    private static OneTimeRow Row(DownloadOptionsViewModel options, OneTimeChoice choice) => options.OneTimeRows.Single(r => r.Choice == choice);

    // The Apple app's series sheet: This Episode, Season N and All Episodes, each with its line, opening on This Episode.
    [Fact]
    public async Task ASeriesDialog_OffersThisEpisode_ItsSeason_AndAllEpisodes_OpeningOnThisEpisode()
    {
        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);

        Assert.True(options.ShowsOneTimeRows);
        Assert.Equal([("S02E03 · Chapter 3", "This episode only", true), ("Season 2", "3 episodes", true), ("All episodes", "Every season", true)],
            options.OneTimeRows.Select(r => (r.Title, r.Detail, r.IsAvailable)));
        Assert.Equal(OneTimeChoice.Episode, options.SelectedOneTimeRow?.Choice);
        Assert.Equal(("Download: Example Series · S2 · E3", "2.0 GB", true), (options.Title, options.SizeText, options.CanStart));
    }

    // As the Apple app's sheet: a row that cannot be chosen is passed over for the first that can.
    [Fact]
    public async Task ThisEpisodeOnThisPc_TheDialogOpensOnItsSeason_AndWithTheSeasonHereToo_OnAllEpisodes()
    {
        await _h.AddAsync("dl_9", DownloadState.Completed, mediaItemId: "ep_2_3");
        var season = await SeriesDialogAsync(HalfWatched, PlaysThird);
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_2_1");
        await _h.AddAsync("dl_2", DownloadState.Completed, mediaItemId: "ep_2_2");
        var all = await SeriesDialogAsync(HalfWatched, PlaysThird);

        Assert.Equal((OneTimeChoice.Season, "Download: Example Series · Season 2"), (season.SelectedOneTimeRow?.Choice, season.Title));
        Assert.Equal((OneTimeChoice.AllEpisodes, "Download: Example Series · All episodes"), (all.SelectedOneTimeRow?.Choice, all.Title));
    }

    [Fact]
    public async Task ThisEpisode_DownloadsThatEpisodeAlone_WithoutTheSwitch()
    {
        Creates("ep_2_3");
        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);

        options.SelectedOneTimeRow = Row(options, OneTimeChoice.Episode);

        Assert.Equal(("Download: Example Series · S2 · E3", "2.0 GB", false), (options.Title, options.SizeText, options.ShowsOnlyUnwatched));
        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_2_3"], CreatedEpisodes());
        Assert.Equal("f_2_3", JsonDocument.Parse(_h.Silo.SentTo(HttpMethod.Post, CreatePath).Single().Body!).RootElement.GetProperty("media_file_id").GetString());
    }

    // The tester's case: the season they are in, from the episode in progress to its end.
    [Fact]
    public async Task TheSeasonRow_DownloadsThatSeason_WithoutItsWatchedEpisodes()
    {
        Creates("ep_2_2", "ep_2_3");
        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);

        options.SelectedOneTimeRow = Row(options, OneTimeChoice.Season);

        Assert.Equal(("Download: Example Series · Season 2", "2 episodes · 3.0 GB", true), (options.Title, options.SizeText, options.ShowsOnlyUnwatched));
        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_2_2", "ep_2_3"], CreatedEpisodes());
    }

    [Fact]
    public async Task BackOnAllEpisodes_TheDialogCountsTheWholeSeriesAgain()
    {
        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);

        options.SelectedOneTimeRow = Row(options, OneTimeChoice.Episode);
        options.SelectedOneTimeRow = Row(options, OneTimeChoice.AllEpisodes);

        Assert.Equal(("Download: Example Series · All episodes", "3 episodes · 4.0 GB", true), (options.Title, options.SizeText, options.ShowsOnlyUnwatched));
        Assert.Empty(options.Versions);
    }

    // A list's choice written back as nothing, as a list does while its rows change, leaves the choice as it was.
    [Fact]
    public async Task NoRowWrittenBack_KeepsTheChoice()
    {
        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);
        options.SelectedOneTimeRow = Row(options, OneTimeChoice.Season);

        options.SelectedOneTimeRow = null;

        Assert.Equal("Download: Example Series · Season 2", options.Title);
    }

    [Fact]
    public async Task ThisEpisodeOnThisPc_SaysSo_AndCannotBeStarted()
    {
        await _h.AddAsync("dl_9", DownloadState.Completed, mediaItemId: "ep_2_3");
        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);

        var episode = Row(options, OneTimeChoice.Episode);
        Assert.Equal(("On this PC", false), (episode.Detail, episode.IsAvailable));

        options.SelectedOneTimeRow = episode;

        Assert.False(options.CanStart);
        Assert.False(await options.StartAsync());
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Post, CreatePath));
    }

    [Fact]
    public async Task ThisEpisodeStillComing_SaysDownloading()
    {
        await _h.AddAsync("dl_9", DownloadState.Downloading, mediaItemId: "ep_2_3");
        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);

        var episode = Row(options, OneTimeChoice.Episode);
        Assert.Equal(("Downloading", false), (episode.Detail, episode.IsAvailable));
    }

    [Fact]
    public async Task ASeasonAllOnThisPc_SaysSo_AndCannotBeStarted()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_2_1");
        await _h.AddAsync("dl_2", DownloadState.Completed, mediaItemId: "ep_2_2");
        await _h.AddAsync("dl_3", DownloadState.Completed, mediaItemId: "ep_2_3");
        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);

        var season = Row(options, OneTimeChoice.Season);
        Assert.Equal(("On this PC", false), (season.Detail, season.IsAvailable));

        options.SelectedOneTimeRow = season;

        Assert.False(options.CanStart);
    }

    [Fact]
    public async Task InGerman_TheRowsSpeakTheAppleAppsWords()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        await _h.AddAsync("dl_9", DownloadState.Completed, mediaItemId: "ep_2_3");

        var options = await SeriesDialogAsync(HalfWatched, PlaysThird);

        Assert.Equal([("S02F03 · Chapter 3", "Auf diesem PC"), ("Season 2", "2 Folgen"), ("Alle Folgen", "Alle Staffeln")],
            options.OneTimeRows.Select(r => (r.Title, r.Detail)));
        Assert.Equal("Nur ungesehene Folgen", DownloadOptionsViewModel.OnlyUnwatchedLabel);
    }

    // A season's dialog has no rows: it is that season.
    [Fact]
    public async Task ASeasonsDialog_HasNoRows()
    {
        var options = await SeasonDialogAsync(Episode(1, 1), Episode(1, 2));

        Assert.False(options.ShowsOneTimeRows);
    }
}
