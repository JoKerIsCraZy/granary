using System.Net;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Downloads;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Download in the ⋯ menu of every film's, episode's and season's card, wherever the card shows: the title page's entries
/// while the server lets the profile download, the status and Delete download for a title on this PC, a menu that reads
/// nothing from the server, and a dialog that opens at once and reads the title's files while it shows.
/// </summary>
public sealed class CardDownloadsTests : IAsyncDisposable
{
    private const string CreatePath = "api/v2/downloads";

    private static readonly MediaCard Movie = new("mv_1", CatalogItemTypes.Movie, "Example Movie");
    private static readonly MediaCard Episode = new("ep_5", CatalogItemTypes.Episode, "Example Series") { EpisodeTitle = "Chapter Five" };
    private static readonly MediaCard Season = new("se_1", CatalogItemTypes.Season, "Season 1");

    private readonly DownloadHarness _h = new();
    private readonly FakeDownloadOptionsDialog _dialog = new();
    private readonly FakeConfirmation _confirmation = new();
    private readonly RecordingNavigation _navigation = new();

    public CardDownloadsTests()
    {
        _h.Silo.On(HttpMethod.Get, ItemDetailFixture.ItemPath("mv_1"), ItemDetailFixture.Ok(TitlePayloads.Movie))
            .On(HttpMethod.Get, ItemDetailFixture.ItemPath("ep_5"), ItemDetailFixture.Ok(TitlePayloads.Episode))
            .On(HttpMethod.Get, ItemDetailFixture.ItemPath("se_1"), ItemDetailFixture.Ok(TitlePayloads.Season))
            .On(HttpMethod.Get, ItemDetailFixture.SeasonItemEpisodesPath("se_1"), ItemDetailFixture.Ok(TitlePayloads.SeasonEpisodes));
    }

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    // The cards' entries over the harness, once the capability was read, as the window reads it on the way to any page.
    private async Task<CardDownloads> CardsAsync(DownloadQueue? queue = null)
    {
        var feature = _h.NewFeature();
        var manager = _h.NewManager(queue, feature);
        var ui = new ImmediateDispatcher();
        var actions = new DownloadActions(manager, feature, _dialog, _confirmation, _navigation, ui,
            () => new DownloadOptionsViewModel(manager, feature, _h.Preferences, _h.Disk, ui, _h.Time, _h.Logs.For<DownloadOptionsViewModel>()),
            _h.Logs.For<DownloadActions>());
        await feature.RefreshAsync(Token);
        return new CardDownloads(actions, new DownloadOfferReader(new CatalogApi(_h.Silo.Http), _h.Session, manager), _h.Logs.For<CardDownloads>());
    }

    private static List<string> Labels(CardDownloads cards, MediaCard card) => [.. cards.EntriesFor(card).Select(e => e.Label)];

    private void DownloadsOff() =>
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(enabled: true, allowed: false)));

    private void Creates(params Func<Task<HttpResponseMessage>>[] answers) => _h.Silo.On(HttpMethod.Post, CreatePath, answers);

    private static Func<Task<HttpResponseMessage>> Created(string entry) =>
        ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, entry));

    private List<JsonElement> CreateBodies() =>
        [.. _h.Silo.SentTo(HttpMethod.Post, CreatePath).Select(r => JsonDocument.Parse(r.Body!).RootElement)];

    // The card's first entry, as its menu runs it: with the card, awaited when it runs asynchronously.
    private static Task ChooseAsync(CardDownloads cards, MediaCard card, int entry = 0) => RunAsync(cards.EntriesFor(card)[entry].Command, card);

    private static Task RunAsync(ICommand command, object? parameter)
    {
        if (command is IAsyncRelayCommand asyncCommand)
        {
            return asyncCommand.ExecuteAsync(parameter);
        }
        command.Execute(parameter);
        return Task.CompletedTask;
    }

    // The viewer waits in the dialog until it has read what to offer, then presses Download.
    private void PressDownloadOnceRead() => _dialog.WhileShown = async options =>
    {
        await options.Reading;
        await options.StartAsync();
    };

    // The viewer waits in the dialog until it has read what to offer, and looks.
    private void WaitForTheOffer() => _dialog.WhileShown = options => options.Reading;

    [Fact]
    public async Task FilmAndEpisodeCards_OfferDownload_AndASeasonCard_DownloadSeason()
    {
        var cards = await CardsAsync();

        Assert.Equal(["Download"], Labels(cards, Movie));
        Assert.Equal(["Download"], Labels(cards, Episode));
        Assert.Equal(["Download season"], Labels(cards, Season));
    }

    [Fact]
    public async Task OtherCards_GetNoDownload()
    {
        var cards = await CardsAsync();

        Assert.Empty(cards.EntriesFor(new MediaCard("sr_1", CatalogItemTypes.Series, "Example Series")));
        // A movie's own card in its box set's row offers no actions: the page's buttons hold them.
        Assert.Empty(cards.EntriesFor(Movie with { CurrentMark = CurrentMark.ThisFilm }));
        Assert.Empty(cards.EntriesFor(new MediaCard("col_1", LibraryCollectionsViewModel.CardType, "Weekend")));
        Assert.Empty(cards.EntriesFor(new MediaCard("tmdb:movie:1", RequestsViewModel.CardType, "Example Movie")));
        Assert.Empty(cards.EntriesFor(MediaCard.Placeholder));
    }

    [Fact]
    public async Task NoCardOffersDownload_WhileTheServerDoesNotLetTheProfileDownload()
    {
        DownloadsOff();

        var cards = await CardsAsync();

        Assert.Empty(cards.EntriesFor(Movie));
        Assert.Empty(cards.EntriesFor(Episode));
        Assert.Empty(cards.EntriesFor(Season));
    }

    [Fact]
    public async Task OpeningTheMenu_AsksTheServerNothing()
    {
        var cards = await CardsAsync();
        int sent = _h.Silo.Sent.Count;

        foreach (var card in new[] { Movie, Episode, Season })
        {
            cards.EntriesFor(card);
        }

        Assert.Equal(sent, _h.Silo.Sent.Count);
    }

    [Fact]
    public async Task WhileDownloading_TheCardShowsTheProgress_WhichOpensTheDownloadsPage()
    {
        await _h.AddAsync("dl_1", mediaItemId: "mv_1");
        var body = _h.ServeGated("dl_1", DownloadPayloads.Source(3000));
        var queue = _h.StartQueue();
        var cards = await CardsAsync(queue);

        body.Release(1260);

        await Until(() => Labels(cards, Movie) is ["Downloading · 42%", "Delete download"], "the card showed the progress");
        await ChooseAsync(cards, Movie);
        Assert.Equal([AppRoute.Downloads], _navigation.Sections);
        Assert.Empty(_dialog.Shown);
    }

    [Fact]
    public async Task ADownloadedCard_OffersDeleteDownload_AndAsksFirst()
    {
        await _h.AddAsync("dl_5", DownloadState.Completed, mediaItemId: "ep_5");
        var cards = await CardsAsync();
        Assert.Equal(["Downloaded", "Delete download"], Labels(cards, Episode));

        _confirmation.Answer = false;
        await ChooseAsync(cards, Episode, entry: 1);
        Assert.NotNull(_h.Find("dl_5"));

        _confirmation.Answer = true;
        await ChooseAsync(cards, Episode, entry: 1);

        Assert.Equal(("Delete download?", "This removes the downloaded copy from this PC. Nothing is deleted from your Silo server.", "Delete"),
            _confirmation.Asked[0]);
        Assert.Null(_h.Find("dl_5"));
        Assert.Equal(["Download"], Labels(cards, Episode));
    }

    [Fact]
    public async Task ADownloadedCard_KeepsItsEntries_WhenTheServerStoppedDownloads()
    {
        DownloadsOff();
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "mv_1");

        var cards = await CardsAsync();

        Assert.Equal(["Downloaded", "Delete download"], Labels(cards, Movie));
    }

    [Fact]
    public async Task ACardsDownload_OpensTheDialogAtOnce_AndReadsTheTitlesFilesWhileItShows()
    {
        var (held, gate) = ItemDetailFixture.Held();
        _h.Silo.On(HttpMethod.Get, ItemDetailFixture.ItemPath("mv_1"), held);
        (bool Reading, string Title, string Text, bool CanStart)? whileReading = null;
        _dialog.WhileShown = async options =>
        {
            whileReading = (options.IsReading, options.Title, options.DescriptionText, options.CanStart);
            gate.SetResult(await ItemDetailFixture.Ok(TitlePayloads.Movie)());
            await options.Reading;
        };
        var cards = await CardsAsync();

        await ChooseAsync(cards, Movie);

        Assert.Equal((true, "Download", "Loading the latest item details…", false), whileReading);
        var shown = Assert.Single(_dialog.Shown);
        await shown.SpaceRead;
        Assert.Equal(("Download: Example Movie", "4567", "8.0 GB"), (shown.Title, shown.SelectedVersion?.FileId, shown.SizeText));
        Assert.Equal((false, DownloadOptionsViewModel.Description, true), (shown.IsReading, shown.DescriptionText, shown.CanStart));
    }

    [Fact]
    public async Task AnEpisodeCardsDownload_DownloadsTheEpisodeOfItsSeries()
    {
        Creates(Created(DownloadPayloads.Entry("dl_5", "ready", episodeId: "ep_5")));
        PressDownloadOnceRead();
        var cards = await CardsAsync();

        await ChooseAsync(cards, Episode);

        Assert.Equal("Download: Example Series · S1 · E5", Assert.Single(_dialog.Shown).Title);
        var body = Assert.Single(CreateBodies());
        Assert.Equal(("sr_1", "ep_5", "4575"), (body.GetProperty("content_id").GetString(), body.GetProperty("episode_id").GetString(),
            body.GetProperty("media_file_id").GetString()));
        var record = _h.Find("dl_5")!.Record;
        Assert.Equal(("Chapter Five", "Example Series", 1, 5), (record.Title, record.SeriesTitle, record.SeasonNumber, record.EpisodeNumber));
    }

    [Fact]
    public async Task ASeasonCardsDownload_OffersTheEpisodesNotOnThisPC()
    {
        Creates(Created(DownloadPayloads.Entry("dl_5", "ready", episodeId: "ep_5")));
        PressDownloadOnceRead();
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1");
        var cards = await CardsAsync();

        await ChooseAsync(cards, Season);

        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal(("Download: Example Series · Season 1", "1 episode · 2.0 GB"), (shown.Title, shown.SizeText));
        var body = Assert.Single(CreateBodies());
        Assert.Equal(("sr_1", "ep_5", "4575"), (body.GetProperty("content_id").GetString(), body.GetProperty("episode_id").GetString(),
            body.GetProperty("media_file_id").GetString()));
    }

    [Fact]
    public async Task ATitleWithoutAFile_SaysSoInTheDialog_AndDownloadStaysOff()
    {
        _h.Silo.On(HttpMethod.Get, ItemDetailFixture.ItemPath("mv_1"), ItemDetailFixture.Ok(TitlePayloads.MovieWithoutFiles));
        PressDownloadOnceRead();
        var cards = await CardsAsync();

        await ChooseAsync(cards, Movie);

        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal(("Download", "No media files for this item.", false, false), (shown.Title, shown.DescriptionText, shown.CanStart, shown.HasOffer));
        Assert.Empty(CreateBodies());
    }

    [Fact]
    public async Task ASeasonWithoutAFile_SaysSoInTheDialog()
    {
        _h.Silo.On(HttpMethod.Get, ItemDetailFixture.SeasonItemEpisodesPath("se_1"), ItemDetailFixture.Ok(TitlePayloads.SeasonEpisodesWithoutFiles));
        WaitForTheOffer();
        var cards = await CardsAsync();

        await ChooseAsync(cards, Season);

        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal(("No media files for this item.", false), (shown.DescriptionText, shown.CanStart));
    }

    [Fact]
    public async Task ASeasonWhoseEpisodesAreAllOnThisPC_SaysSoInTheDialog()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1");
        await _h.AddAsync("dl_5", DownloadState.Completed, mediaItemId: "ep_5");
        WaitForTheOffer();
        var cards = await CardsAsync();

        await ChooseAsync(cards, Season);

        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal(("Every episode of this season is on this PC.", false), (shown.DescriptionText, shown.CanStart));
    }

    [Fact]
    public async Task AReadThatFails_SaysSo_AndTryAgainReadsAgain()
    {
        _h.Silo.On(HttpMethod.Get, ItemDetailFixture.ItemPath("mv_1"), ItemDetailFixture.Busy(), ItemDetailFixture.Ok(TitlePayloads.Movie));
        (string Text, bool Failed, bool CanStart)? failed = null;
        _dialog.WhileShown = async options =>
        {
            await options.Reading;
            failed = (options.DescriptionText, options.ReadFailed, options.CanStart);
            await options.TryAgainCommand.ExecuteAsync(null);
        };
        var cards = await CardsAsync();

        await ChooseAsync(cards, Movie);

        Assert.Equal(("The item details could not be loaded.", true, false), failed);
        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal(("Download: Example Movie", false, true), (shown.Title, shown.ReadFailed, shown.CanStart));
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, ItemDetailFixture.ItemPath("mv_1")).Count);
    }

    [Fact]
    public async Task ClosingTheDialog_StopsTheRead_AndShowsNothingOfIt()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.OnCancellable(HttpMethod.Get, ItemDetailFixture.ItemPath("mv_1"), async cancellationToken =>
        {
            await using (cancellationToken.Register(() => stopped.TrySetResult()))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        // The viewer cancels the dialog at once.
        _dialog.WhileShown = _ => Task.CompletedTask;
        var cards = await CardsAsync();

        await ChooseAsync(cards, Movie);

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var shown = Assert.Single(_dialog.Shown);
        await shown.Reading;
        Assert.Equal(("Download", false, false), (shown.Title, shown.HasOffer, shown.CanStart));
    }
}
