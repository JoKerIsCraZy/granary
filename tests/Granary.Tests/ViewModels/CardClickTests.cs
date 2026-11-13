using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>
/// What a click on a card's button does (<see cref="CardClick"/>): the card's own command, a pick while its grid selects,
/// or, when the click ends a pointer's press on a title line that opens its series (<see cref="HeadingPress"/>), the series.
/// </summary>
public sealed class CardClickTests
{
    private static readonly Uri Server = new("https://silo.test/media");

    private static readonly MediaCard Episode = MediaCardFactory.Poster(new CatalogItem
    {
        ContentId = "ep-1", Type = CatalogItemTypes.Episode, Title = "Safe Harbor", SeriesTitle = "Tracker", SeriesId = "series-1",
        SeasonNumber = 4, EpisodeNumber = 1,
    }, Server);

    private static readonly MediaCard Movie = MediaCardFactory.Poster(
        new CatalogItem { ContentId = "movie-1", Type = CatalogItemTypes.Movie, Title = "Masterplan", Year = 2026 }, Server);

    private readonly ScriptedSilo _silo = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly LogSink _logs = new();
    private readonly List<MediaCard> _opened = [];
    private readonly HeadingPress _press = new();
    private readonly CardSelection _selection = new();
    private readonly MediaCardActions _actions;
    private readonly RelayCommand<MediaCard> _open;

    public CardClickTests()
    {
        var session = new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
        _actions = new MediaCardActions(new CatalogApi(_silo.Http), session, _navigation, _logs.For<MediaCardActions>(),
            new ViewerStateChanges(new PlaybackStops()));
        _open = new RelayCommand<MediaCard>(card => _opened.Add(card!));
    }

    // A click as a row or grid runs it: the card asks its press first, as MediaCardView.TryOpenHeading.
    private void Click(MediaCard card, CardSelection? selection = null) =>
        CardClick.Run(card, _press.TryOpen(card, _actions, selection is { IsActive: true }), _open, selection);

    [Fact]
    public void AClickThatEndsAPressOnAnEpisodesTitle_OpensTheSeries_NotTheCard()
    {
        _press.Begin(Episode, CardLines.Title);

        Click(Episode);

        var (route, parameter) = Assert.Single(_navigation.Navigations);
        Assert.Equal(AppRoute.Item, route);
        Assert.Equal("series-1", parameter);
        Assert.Empty(_opened);
    }

    [Fact]
    public void AClickWithoutSuchAPress_RunsTheCardsOwnCommand()
    {
        Click(Episode);

        Assert.Equal([Episode], _opened);
        Assert.Empty(_navigation.Navigations);
    }

    [Theory]
    [InlineData(CardLines.EpisodeTitle)]
    [InlineData(CardLines.Subtitle)]
    [InlineData(CardLines.None)]
    public void APressOnAnotherLineOrOnTheArtwork_OpensTheCard(CardLines line)
    {
        _press.Begin(Episode, line);

        Click(Episode);

        Assert.False(_press.IsPending);
        Assert.Equal([Episode], _opened);
        Assert.Empty(_navigation.Navigations);
    }

    [Fact]
    public void APressOnATitleThatIsTheCardsOwn_OpensTheCard()
    {
        _press.Begin(Movie, CardLines.Title);

        Click(Movie);

        Assert.Equal([Movie], _opened);
        Assert.Empty(_navigation.Navigations);
    }

    [Fact]
    public void APressIsUsedOnce_TheNextClickOpensTheCard()
    {
        // The pointer's click took it; Enter on the same card afterwards is a plain click.
        _press.Begin(Episode, CardLines.Title);
        Click(Episode);

        Click(Episode);

        Assert.Single(_navigation.Navigations);
        Assert.Equal([Episode], _opened);
    }

    [Fact]
    public void APressThatEndedWithoutAClick_IsForgotten_AKeysClickOpensTheCard()
    {
        // The pointer left, the press was cancelled, or a key came to the button: the card clears the press.
        _press.Begin(Episode, CardLines.Title);
        Assert.True(_press.IsPending);

        _press.Clear();
        Click(Episode);

        Assert.Equal([Episode], _opened);
        Assert.Empty(_navigation.Navigations);
    }

    [Fact]
    public void ANewPress_ForgetsTheOneBefore()
    {
        _press.Begin(Episode, CardLines.Title);

        _press.Begin(Episode, CardLines.Subtitle);
        Click(Episode);

        Assert.Equal([Episode], _opened);
        Assert.Empty(_navigation.Navigations);
    }

    [Fact]
    public void APressOnOneCard_IsNotAnothersClick()
    {
        // A container given another title between the press and the click.
        var other = Episode with { Id = "ep-2", HeadingId = "series-2" };
        _press.Begin(Episode, CardLines.Title);

        Click(other);

        Assert.Equal([other], _opened);
        Assert.Empty(_navigation.Navigations);
        Assert.False(_press.IsPending);
    }

    [Fact]
    public void WhileTheGridSelects_AClickPicksTheCard_AlsoOnItsTitle()
    {
        _selection.Start();
        _press.Begin(Episode, CardLines.Title, isSelecting: true);
        Assert.False(_press.IsPending);

        Click(Episode, _selection);

        Assert.True(_selection.Contains(Episode.Id));
        Assert.Empty(_opened);
        Assert.Empty(_navigation.Navigations);
    }

    [Fact]
    public void APressFromBeforeTheGridSelected_PicksToo()
    {
        _press.Begin(Episode, CardLines.Title);
        _selection.Start();

        Click(Episode, _selection);

        Assert.True(_selection.Contains(Episode.Id));
        Assert.Empty(_navigation.Navigations);
    }

    [Fact]
    public void AGridThatDoesNotSelect_RunsTheCommand_AsARowDoes()
    {
        Click(Movie, _selection);

        Assert.Equal([Movie], _opened);
        Assert.False(_selection.Contains(Movie.Id));
    }

    [Fact]
    public void ACommandThatCannotRun_AndNoCommand_DoNothing()
    {
        var refused = new RelayCommand<MediaCard>(card => _opened.Add(card!), _ => false);

        CardClick.Run(Movie, openedHeading: false, refused);
        CardClick.Run(Movie, openedHeading: false, invoked: null);

        Assert.Empty(_opened);
    }
}
