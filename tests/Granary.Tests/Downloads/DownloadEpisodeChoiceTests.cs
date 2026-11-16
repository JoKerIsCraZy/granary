using System.Net;
using System.Text.Json;
using Granary.Core.Api.Catalog;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Which episodes a season's or a series' download dialog starts: "Only unwatched episodes" leaves out the ones the viewer
/// watched, and keeps the one in progress.
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

    private async Task<DownloadOptionsViewModel> SeriesDialogAsync(IReadOnlyList<(Season, IReadOnlyList<Episode>)> seasons)
    {
        var offer = DownloadOffers.ForSeries(new CatalogItemDetail { Title = "Example Series" }, "sr_1", seasons, _ => false)!;
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

    // The switch starts on whenever it shows: a season watched to the end then has nothing to start until it is turned off.
    [Fact]
    public async Task ASeasonWhoseEpisodesLeftAreAllWatched_StartsNothing_UntilOnlyUnwatchedIsTurnedOff()
    {
        Creates("ep_1_1", "ep_1_2");
        var options = await SeasonDialogAsync(Episode(1, 1, Watched), Episode(1, 2, Watched));

        Assert.Equal(("0 episodes", false), (options.SizeText, options.CanStart));
        Assert.False(await options.StartAsync());
        Assert.Empty(CreatedEpisodes());

        options.OnlyUnwatched = false;

        Assert.Equal(("2 episodes · 2.0 GB", true), (options.SizeText, options.CanStart));
        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_1_1", "ep_1_2"], CreatedEpisodes());
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

        Assert.True(options.ShowsOnlyUnwatched);
        Assert.Equal("2 episodes · 2.0 GB", options.SizeText);
        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_2_1", "ep_2_2"], CreatedEpisodes());
    }
}
