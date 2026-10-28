using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Auth;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using Microsoft.Extensions.DependencyInjection;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

public sealed class ItemDetailViewModelTests
{
    private readonly ItemDetailFixture _fixture = new();

    private static Uri Art(string path) => new(ScriptedSilo.Base.AbsoluteUri.TrimEnd('/') + path);

    [Fact]
    public async Task Movie_ShowsTheHeroLikeTheWeb()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        Assert.Equal(ItemDetailState.Ready, detail.State);
        Assert.True(detail.IsReady);
        Assert.Equal("Masterplan", detail.DisplayTitle);
        Assert.Equal("MOVIE · AMAZON MGM STUDIOS", detail.Overline);
        Assert.Equal("One plan. No second chances.", detail.Tagline);
        Assert.Equal(
            [new DetailChip("2026"), new DetailChip("R"), new DetailChip("1H 49M"), new DetailChip("1080P", true), new DetailChip("EAC3", true)],
            detail.Chips);
        Assert.Equal([new DetailRating("IMDb", "5.4"), new DetailRating("TMDB", "7.1")], detail.Ratings);
        Assert.Equal("Directed by David Ayer · Written by Sylvester Stallone, David Ayer · Action · Thriller", detail.CreditsLine);
        Assert.Equal("A former soldier takes one last job.", detail.Overview);
        Assert.Empty(detail.VersionLines);
        Assert.Empty(detail.Breadcrumbs);
        Assert.True(detail.ShowsPoster);
        Assert.False(detail.IsCompactHero);
        Assert.Equal(Art("/api/v2/artwork/movies/1001/poster/w342.ab12.webp?exp=1767225600&sig=poster-sig"), detail.PosterUrl);
        Assert.Equal(Art("/api/v2/artwork/movies/1001/backdrop/w1280.cd34.webp?exp=1767225600&sig=backdrop-sig"), detail.BackdropUrl);
        Assert.Equal(Art("/api/v2/artwork/movies/1001/logo/w500.ef56.webp?exp=1767225600&sig=logo-sig"), detail.LogoUrl);
        Assert.True(detail.ShowsLogo);
        Assert.False(detail.ShowsTitleText);
    }

    [Fact]
    public async Task Movie_InProgress_OffersResumeAndStartOver()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        Assert.Equal("Resume", detail.PlayLabel);
        Assert.Equal(MovieId, detail.PlayTargetId);
        Assert.True(detail.PlayCommand.CanExecute(null));
        Assert.True(detail.CanStartOver);
        Assert.Equal("Mark Watched", detail.WatchedLabel);
        Assert.True(detail.ShowsWatchedAction);
        Assert.True(detail.CanFavorite);
        Assert.Equal("Add to favorites", detail.FavoriteLabel);
        Assert.Equal("Add to Watchlist", detail.WatchlistLabel);
    }

    [Fact]
    public async Task ACastOrCrewCard_OpensThePersonsPage()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);
        var rivas = detail.People.Single(p => p.Name == "Arianna Rivas");

        detail.OpenPersonCommand.Execute(detail.People.Single(p => p.Name == "Jason Statham"));
        detail.OpenPersonCommand.Execute(rivas);

        Assert.Equal((AppRoute.Person, (object?)"976"), Assert.Single(_fixture.Navigation.Navigations));
        Assert.False(rivas.CanOpen);
    }

    [Fact]
    public async Task Movie_ShowsCrewThenCastTrailersAndSimilarTitles()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        Assert.Equal("Cast & Crew", detail.PeopleTitle);
        Assert.Equal(
            [("David Ayer", "Director"), ("Sylvester Stallone", "Writer"), ("Chuck Dixon", "Writer"),
             ("Jason Statham", "Levon Cade"), ("Arianna Rivas", "Jenny"), ("Jason Flemyng", "Gareth")],
            detail.People.Select(p => (p.Name, p.Role ?? "")));
        var statham = detail.People.Single(p => p.Name == "Jason Statham");
        Assert.Equal(Art("/api/v2/artwork/people/976/w185.webp?exp=1&sig=c"), statham.PhotoUrl);
        Assert.Equal("JS", statham.Initials);
        Assert.Null(detail.People.Single(p => p.Name == "Arianna Rivas").PhotoUrl);

        var trailer = Assert.Single(detail.Trailers);
        Assert.Equal("Official Trailer", trailer.Title);
        Assert.Equal("Trailer · Official", trailer.Caption);
        Assert.Equal(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), trailer.WatchUrl);

        // The title itself is not "like itself".
        Assert.Equal(["Beekeeper", "Reacher"], detail.Similar.Select(c => c.Title));
        var similar = Assert.Single(_fixture.Silo.Sent, r => r.Uri.AbsolutePath.EndsWith("/recommendations/similar/" + MovieId, StringComparison.Ordinal));
        Assert.Contains("limit=12", similar.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artwork_IsRequestedForTheDisplayScale()
    {
        _fixture.Movie();
        var detail = _fixture.Create();
        detail.DisplayScale = 1.5;

        await detail.LoadCommand.ExecuteAsync(MovieId);

        string query = _fixture.Silo.Sent.Single(r => r.Uri.AbsolutePath.EndsWith("/catalog/items/" + MovieId, StringComparison.Ordinal)).Uri.Query;
        Assert.Contains("backdrop_width=1920", query, StringComparison.Ordinal);
        Assert.Contains("logo_width=750", query, StringComparison.Ordinal);
        Assert.Contains("poster_width=513", query, StringComparison.Ordinal);
        Assert.Contains("photo_width=278", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Play_OpensThePlayerAtTheResumePoint()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        detail.PlayCommand.Execute(null);

        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(MovieId)), _fixture.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task StartOver_OpensThePlayerAtTheBeginning()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        detail.StartOverCommand.Execute(null);

        Assert.Equal((AppRoute.Player, (object?)PlayRequest.FromStart(MovieId)), _fixture.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task Series_Play_PlaysTheEpisodeToResume()
    {
        var detail = await _fixture.Series().LoadAsync(SeriesId);

        detail.PlayCommand.Execute(null);

        Assert.Equal((AppRoute.Player, (object?)PlayRequest.Resume(EpisodeId)), _fixture.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task AWatchedMovie_PlaysFromTheStart_WithoutAStartOverChoice()
    {
        _fixture.Movie().Silo.On(HttpMethod.Post, "api/v2/watched/" + MovieId, ScriptedSilo.NoContent());
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.Equal("Play", detail.PlayLabel);
        Assert.False(detail.CanStartOver);
        Assert.False(detail.StartOverCommand.CanExecute(null));
    }

    [Fact]
    public async Task Series_ShowsAirYearsCountsAndCreators()
    {
        var detail = await _fixture.Series().LoadAsync(SeriesId);

        Assert.Equal("SERIES · NETFLIX", detail.Overline);
        Assert.Equal("Every story has its gentleman thief.", detail.Tagline);
        // Specials do not count as a season; the regular seasons hold 10 + 7 + 8 episodes.
        Assert.Equal(["2021–2026", "TV-MA", "3 SEASONS", "25 EPISODES"], detail.Chips.Select(c => c.Text));
        Assert.Equal(["IMDb 7.5", "TMDB 7.7", "Rotten Tomatoes 98%"], detail.Ratings.Select(r => r.AccessibleName));
        Assert.Equal("Created by George Kay, François Uzan · Crime · Drama · Mystery", detail.CreditsLine);
        Assert.Equal([("George Kay", "Creator"), ("François Uzan", "Creator"), ("Omar Sy", "Assane Diop")], detail.People.Select(p => (p.Name, p.Role ?? "")));
        Assert.Equal(new Uri("https://cdn.example/lupin-logo.png"), detail.LogoUrl);
        Assert.Equal(["Money Heist"], detail.Similar.Select(c => c.Title));
    }

    [Fact]
    public async Task Series_ListsSeasonsWithTheSpecialsLast()
    {
        var detail = await _fixture.Series().LoadAsync(SeriesId);

        Assert.True(detail.HasSeasons);
        Assert.False(detail.ShowsEpisodesSection);
        Assert.Equal(["Staffel 1", "Staffel 2", "Staffel 3", "Specials"], detail.Seasons.Select(s => s.Title));
        Assert.Equal("4 total", detail.SeasonsTotalText);
        var third = detail.Seasons[2];
        Assert.Equal("4 of 8 episodes", third.Caption);
        Assert.Equal(0.5, third.Progress);
        Assert.False(third.IsWatched);
        Assert.True(detail.Seasons[0].IsWatched);
        Assert.Equal("10 episodes", detail.Seasons[0].Caption);
    }

    [Fact]
    public async Task Series_InProgress_ResumesTheNextEpisode()
    {
        var detail = await _fixture.Series().LoadAsync(SeriesId);

        Assert.Equal("Resume", detail.PlayLabel);
        Assert.Equal(EpisodeId, detail.PlayTargetId);
        Assert.Equal("Mark Series Watched", detail.WatchedLabel);
        Assert.True(detail.IsFavorite);
        Assert.Equal("Remove from favorites", detail.FavoriteLabel);
        Assert.Equal("Remove from Watchlist", detail.WatchlistLabel);
    }

    [Fact]
    public async Task Series_LoadsSeasonsAndSimilarTitlesInParallel()
    {
        var similarAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Get(ItemPath(SeriesId), Ok(Series))
            .Get(SeasonsPath(SeriesId), async () =>
            {
                // Answered only once "More like this" has been asked as well.
                await similarAsked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                return await Ok(SeriesSeasons)();
            })
            .Get(SimilarPath(SeriesId), () =>
            {
                similarAsked.TrySetResult();
                return Ok(SeriesSimilar)();
            })
            .EpisodeLists();

        var detail = await _fixture.LoadAsync(SeriesId);

        Assert.Equal(4, detail.Seasons.Count);
        Assert.Single(detail.Similar);
    }

    [Fact]
    public async Task Series_FailedSeasons_HideOnlyTheSeasonRow()
    {
        _fixture.Series().Get(SeasonsPath(SeriesId), Busy());

        var detail = await _fixture.LoadAsync(SeriesId);

        Assert.True(detail.IsReady);
        Assert.False(detail.HasSeasons);
        Assert.True(detail.HasSimilar);
        Assert.True(detail.HasPeople);
        // The counts fall back to the detail's own.
        Assert.Equal(["2021–2026", "TV-MA", "3 SEASONS", "25 EPISODES"], detail.Chips.Select(c => c.Text));
        Assert.Contains(_fixture.Logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("seasons", StringComparison.Ordinal));
        Assert.DoesNotContain("sig=", _fixture.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Series_FailedSimilarTitles_HideOnlyMoreLikeThis()
    {
        _fixture.Series().Get(SimilarPath(SeriesId), ScriptedSilo.NetworkError());

        var detail = await _fixture.LoadAsync(SeriesId);

        Assert.True(detail.IsReady);
        Assert.False(detail.HasSimilar);
        Assert.Equal(4, detail.Seasons.Count);
    }

    [Fact]
    public async Task Series_PrefetchesTheStillsOfEverySeason_TheSeasonToPlayFirst()
    {
        await _fixture.Series().LoadAsync(SeriesId);

        // Season 3 holds the episode to resume; seasons 1, 2 and the specials follow.
        Assert.Equal(
            [Art("/api/v2/artwork/episodes/1/w400.webp?exp=1&sig=e1"), Art("/api/v2/artwork/episodes/11/w400.webp?exp=1&sig=s11")],
            _fixture.Artwork.Urls);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath("season-96677-0")));
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath("season-96677-2")));
    }

    [Fact]
    public async Task Series_AFailedEpisodeList_SkipsOnlyThatSeasonsStills()
    {
        var detail = await _fixture.Series().Get(SeasonItemEpisodesPath(Season3Id), Busy()).LoadAsync(SeriesId);

        Assert.Equal([Art("/api/v2/artwork/episodes/11/w400.webp?exp=1&sig=s11")], _fixture.Artwork.Urls);
        Assert.Equal(ItemDetailState.Ready, detail.State);
        Assert.Equal(4, detail.Seasons.Count);
    }

    [Fact]
    public async Task Series_LeavingThePage_LetsThePrefetchFinish()
    {
        // Opening a season leaves the series page; the stills of the other seasons should keep coming.
        var (held, gate) = Held();
        _fixture.Series().Get(SeasonItemEpisodesPath("season-96677-1"), held);
        var detail = _fixture.Create();

        var load = detail.LoadCommand.ExecuteAsync(SeriesId);
        await AuthPipeline.Eventually(() => _fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath("season-96677-1")).Count == 1);
        detail.LoadCommand.Cancel();
        gate.SetResult(await Ok(SeasonOneEpisodes)());
        await load;

        Assert.Contains(Art("/api/v2/artwork/episodes/11/w400.webp?exp=1&sig=s11"), _fixture.Artwork.Urls);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath("season-96677-0")));
    }

    [Fact]
    public async Task SingleSeasonSeries_PrefetchesNothing_ItsEpisodesAreOnThePage()
    {
        _fixture.Series(SingleSeason).Get(SeasonItemEpisodesPath("season-96677-1"), Ok(SeasonEpisodes));

        await _fixture.LoadAsync(SeriesId);

        Assert.Empty(_fixture.Artwork.Urls);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath("season-96677-1")));
    }

    [Fact]
    public async Task SingleSeasonSeries_ListsItsEpisodesInsteadOfOneSeasonCard()
    {
        _fixture.Series(SingleSeason).Get(SeasonItemEpisodesPath("season-96677-1"), Ok(SeasonEpisodes));

        var detail = await _fixture.LoadAsync(SeriesId);

        Assert.False(detail.HasSeasons);
        Assert.True(detail.ShowsEpisodesSection);
        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2"], detail.Episodes.Select(e => e.Card.Title));
        Assert.Equal("2 total", detail.EpisodesTotalText);
        Assert.Equal(["2021–2026", "TV-MA", "1 SEASON", "2 EPISODES"], detail.Chips.Select(c => c.Text));
    }

    [Fact]
    public async Task SingleSeasonSeries_FailedEpisodes_FallBackToTheSeasonCard()
    {
        _fixture.Series(SingleSeason).Get(SeasonItemEpisodesPath("season-96677-1"), Busy());

        var detail = await _fixture.LoadAsync(SeriesId);

        Assert.Equal(["Season 1"], detail.Seasons.Select(s => s.Title));
        Assert.False(detail.ShowsEpisodesSection);
    }

    [Fact]
    public async Task Season_ShowsTheSeriesTitleBreadcrumbAndEpisodeGrid()
    {
        var detail = await _fixture.Season().LoadAsync(Season3Id);

        Assert.Equal("Lupin", detail.DisplayTitle);
        Assert.Equal("Staffel 3", detail.Subtitle);
        Assert.Equal("", detail.Overline);
        Assert.True(detail.ShowsTitleText);
        Assert.True(detail.IsCompactHero);
        Assert.Equal([new DetailCrumb("Lupin", SeriesId, true), new DetailCrumb("Season 3", null, false)], detail.Breadcrumbs);
        Assert.Equal(["2026", "SEASON 3", "8 EPISODES"], detail.Chips.Select(c => c.Text));
        Assert.Equal("Assane faces his most personal heist yet.", detail.Overview);
        Assert.Equal("Mark Season Watched", detail.WatchedLabel);
        Assert.False(detail.CanFavorite);
        Assert.Empty(detail.CreditParts);
        Assert.Equal("Cast", detail.PeopleTitle);
        Assert.Equal(["Omar Sy"], detail.People.Select(p => p.Name));

        Assert.Equal("8 total", detail.EpisodesTotalText);
        var first = detail.Episodes[0];
        Assert.Equal("S03E01 · Chapter 1", first.Card.Title);
        Assert.Equal("23 Oct 2026 · 52m", first.Card.Subtitle);
        Assert.Null(first.Card.Caption);
        Assert.Equal("Assane returns to Paris.", first.Overview);
        Assert.True(first.Card.IsWatched);
        Assert.Equal("S03E01 · Chapter 1, 23 Oct 2026 · 52m, watched", first.AccessibleName);
        Assert.Equal(0.5, detail.Episodes[1].Card.Progress);
        Assert.Null(detail.Episodes[1].Overview);
        Assert.DoesNotContain(_fixture.Silo.Sent, r => r.Uri.AbsolutePath.Contains("/recommendations/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Season_Unstarted_PlaysTheFirstEpisode()
    {
        var detail = await _fixture.Season().LoadAsync(Season3Id);

        Assert.Equal("Play First Episode", detail.PlayLabel);
        Assert.Equal(EpisodeId, detail.PlayTargetId);
    }

    [Fact]
    public async Task Season_FailedEpisodes_HideOnlyTheGrid()
    {
        _fixture.Season().Get(SeasonItemEpisodesPath(Season3Id), Busy());

        var detail = await _fixture.LoadAsync(Season3Id);

        Assert.True(detail.IsReady);
        Assert.False(detail.ShowsEpisodesSection);
        Assert.Equal(["2026", "SEASON 3", "8 EPISODES"], detail.Chips.Select(c => c.Text));
        Assert.Equal(EpisodeId, detail.PlayTargetId);
    }

    [Fact]
    public async Task Season_WithoutEpisodes_SaysSo()
    {
        _fixture.Season().Get(SeasonItemEpisodesPath(Season3Id), Ok("""{ "items": [] }"""));

        var detail = await _fixture.LoadAsync(Season3Id);

        Assert.True(detail.ShowsNoEpisodes);
        Assert.True(detail.ShowsEpisodesSection);
        Assert.False(detail.HasEpisodes);
    }

    [Fact]
    public async Task Episode_ShowsItsStillCodeDateAndRuntime()
    {
        var detail = await _fixture.Episode().LoadAsync(EpisodeId);

        Assert.Equal("S03E01 · Chapter 1", detail.DisplayTitle);
        Assert.Equal("Chapter 1", detail.Title);
        Assert.False(detail.ShowsPoster);
        // The series' logo would hide which episode this is.
        Assert.Null(detail.LogoUrl);
        Assert.True(detail.ShowsTitleText);
        Assert.Equal(Art("/api/v2/artwork/episodes/1/still/w1280.webp?exp=1&sig=still"), detail.BackdropUrl);
        Assert.Equal(
            [new DetailChip("52M"), new DetailChip("23 OCT 2026"), new DetailChip("4K HDR", true), new DetailChip("TRUEHD", true)],
            detail.Chips);
        Assert.Equal(["4K HDR · HEVC · TRUEHD", "DIRECTOR'S CUT · 1080P · H264 · EAC3"], detail.VersionLines);
        Assert.Equal("Directed by Ludovic Bernard · Written by George Kay", detail.CreditsLine);
        Assert.Equal(["Ludovic Bernard", "George Kay", "Omar Sy", "Guest Star"], detail.People.Select(p => p.Name));
        Assert.Equal("Play", detail.PlayLabel);
        Assert.Equal(EpisodeId, detail.PlayTargetId);
        Assert.Equal("Mark Unwatched", detail.WatchedLabel);
        Assert.False(detail.CanFavorite);
    }

    [Fact]
    public async Task Episode_LinksItsSeasonAndListsTheOtherEpisodes()
    {
        var detail = await _fixture.Episode().LoadAsync(EpisodeId);

        Assert.Equal(
            [new DetailCrumb("Lupin", SeriesId, true), new DetailCrumb("Staffel 3", Season3Id, false), new DetailCrumb("Episode 1", null, false)],
            detail.Breadcrumbs);
        // The episode itself is left out.
        Assert.Equal(["S03E02 · Chapter 2"], detail.MoreEpisodes.Select(c => c.Title));
    }

    [Fact]
    public async Task Episode_FailedSeasonList_KeepsAnUnlinkedSeasonCrumb()
    {
        _fixture.Episode().Get(SeasonsPath(SeriesId), Busy()).Get(SeriesEpisodesPath(SeriesId, 3), Busy());

        var detail = await _fixture.LoadAsync(EpisodeId);

        Assert.True(detail.IsReady);
        Assert.Equal(new DetailCrumb("Season 3", null, false), detail.Breadcrumbs[1]);
        Assert.False(detail.HasMoreEpisodes);
    }

    [Fact]
    public async Task UnreadableImageSizes_StillShowTheTitleAtDefaultSizes()
    {
        _fixture.Movie().Get("api/v2/images/capabilities", Ok("not json"));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.True(detail.IsReady);
        var item = _fixture.Silo.Sent.Single(r => r.Uri.AbsolutePath.EndsWith("/catalog/items/" + MovieId, StringComparison.Ordinal));
        Assert.Equal("", item.Uri.Query);
    }

    [Fact]
    public async Task NotFound_ShowsTheUnavailableState()
    {
        _fixture.Get(ItemPath("movie-gone"), ScriptedSilo.Problem(404, ProblemCodes.NotFound));

        var detail = await _fixture.LoadAsync("movie-gone");

        Assert.Equal(ItemDetailState.NotFound, detail.State);
        Assert.True(detail.IsNotFound);
        Assert.False(detail.HasLoadError);
        Assert.False(detail.IsReady);
        Assert.Equal("This title is no longer available", ItemDetailViewModel.NotFoundTitle);
    }

    [Fact]
    public async Task FailedLoad_OffersRetry()
    {
        _fixture.Movie().Get(ItemPath(MovieId), Busy(), Ok(Movie));
        var detail = await _fixture.LoadAsync(MovieId);

        Assert.True(detail.HasLoadError);
        Assert.Equal("The server is busy, try again.", detail.LoadErrorText);

        await detail.RetryCommand.ExecuteAsync(null);

        Assert.True(detail.IsReady);
        Assert.Equal("", detail.LoadErrorText);
        Assert.Equal("Masterplan", detail.DisplayTitle);
    }

    [Fact]
    public async Task UnreachableServer_SaysSo()
    {
        _fixture.Get(ItemPath(MovieId), ScriptedSilo.NetworkError());

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.True(detail.HasLoadError);
        Assert.Equal("Could not reach the server. Check the connection and try again.", detail.LoadErrorText);
    }

    [Fact]
    public async Task NoActiveServer_FailsWithoutAsking()
    {
        _fixture.Session.ActiveServer = null;

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.True(detail.HasLoadError);
        Assert.Empty(_fixture.Silo.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("..")]
    public async Task NoUsableId_IsNotFound(string? id)
    {
        var detail = await _fixture.LoadAsync(id!);

        Assert.True(detail.IsNotFound);
        Assert.Empty(_fixture.Silo.Sent);
    }

    [Fact]
    public async Task SeasonCard_OpensTheSeason()
    {
        var detail = await _fixture.Series().LoadAsync(SeriesId);

        detail.OpenCardCommand.Execute(detail.Seasons[2]);

        Assert.Equal((AppRoute.Item, (object?)Season3Id), _fixture.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task EpisodeCard_AndSeriesCrumb_OpenTheirItems()
    {
        var detail = await _fixture.Season().LoadAsync(Season3Id);

        detail.OpenEpisodeCommand.Execute(detail.Episodes[1]);
        detail.OpenCrumbCommand.Execute(detail.Breadcrumbs[0]);
        detail.OpenCrumbCommand.Execute(detail.Breadcrumbs[1]);

        Assert.Equal(new (AppRoute, object?)[] { (AppRoute.Item, Episode2Id), (AppRoute.Item, SeriesId) }, _fixture.Navigation.Navigations);
    }

    [Fact]
    public async Task SimilarCard_OpensThatTitle()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        detail.OpenCardCommand.Execute(detail.Similar[0]);

        Assert.Equal((AppRoute.Item, (object?)"movie-tmdb-1002"), Assert.Single(_fixture.Navigation.Navigations));
    }

    [Fact]
    public async Task EpisodePage_OpensTheSeasonAndOtherEpisodesButNotItself()
    {
        var detail = await _fixture.Episode().LoadAsync(EpisodeId);

        detail.OpenCardCommand.Execute(detail.MoreEpisodes[0]);
        detail.OpenCrumbCommand.Execute(new DetailCrumb("Episode 1", EpisodeId, false));
        detail.OpenCrumbCommand.Execute(detail.Breadcrumbs[1]);

        Assert.Equal(new (AppRoute, object?)[] { (AppRoute.Item, Episode2Id), (AppRoute.Item, Season3Id) }, _fixture.Navigation.Navigations);
    }

    [Fact]
    public async Task GoBack_LeavesThroughTheShell()
    {
        _fixture.Navigation.NavigateTo(AppRoute.Home);
        _fixture.Navigation.NavigateTo(AppRoute.Item, "movie-gone");
        _fixture.Get(ItemPath("movie-gone"), ScriptedSilo.Problem(404, ProblemCodes.NotFound));
        var detail = await _fixture.LoadAsync("movie-gone");

        Assert.True(detail.CanGoBack);
        detail.GoBackCommand.Execute(null);

        Assert.Equal(AppRoute.Home, _fixture.Navigation.Current);
    }

    [Fact]
    public async Task FailedLogo_ShowsTheDisplayTitle()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);
        var changed = new List<string?>();
        detail.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        detail.OnLogoFailed();

        Assert.False(detail.ShowsLogo);
        Assert.True(detail.ShowsTitleText);
        Assert.Contains(nameof(ItemDetailViewModel.ShowsTitleText), changed);
    }

    [Fact]
    public async Task Overview_TogglesBetweenMoreAndLess()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);
        Assert.Equal("More", detail.OverviewToggleText);

        detail.ToggleOverviewCommand.Execute(null);

        Assert.True(detail.IsOverviewExpanded);
        Assert.Equal("Less", detail.OverviewToggleText);
    }

    [Fact]
    public void Registration_ResolvesTheViewModel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INavigationService, RecordingNavigation>();
        services.AddSingleton<IConfirmation, FakeConfirmation>();
        services.AddGranaryCore();
        using var provider = services.BuildServiceProvider();

        Assert.NotSame(provider.GetRequiredService<ItemDetailViewModel>(), provider.GetRequiredService<ItemDetailViewModel>());
    }
}
