using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// A cast or crew member's page, as Silo's web shows it (<c>pages/PersonDetail.tsx</c>): photo, name, when and where they
/// were born (and died), the bio, and their titles newest first, All, Movies or Series, paged as the grid scrolls.
/// Refresh metadata queues a provider refresh (an administrator's "Refresh now" waits for it). As the apps do, a person
/// the provider has told little about (no bio, photo or birth date) is refreshed once per visit, and read again every
/// few seconds until the details come, for two minutes at most and no longer than five reads that change nothing.
/// </summary>
public sealed partial class PersonViewModel : ObservableObject
{
    public const int PageSize = 60;

    /// <summary>Reads after a refresh that change nothing before the page stops waiting for it.</summary>
    public const int MaxUnchangedReads = 5;

    public const int MaxRefreshReads = 40;

    public static readonly TimeSpan RefreshPollInterval = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(2);

    private static readonly SettingChoice All = new("all", "All");

    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly AdminApi _admin;
    private readonly ImageParameterCache _images;
    private readonly AccountPermissions _permissions;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly ILogger<PersonViewModel> _logger;
    private (string? ServerId, string? ProfileId, string PersonId)? _shownFor;
    private string? _personId;
    private BrowseCardPager? _pager;
    private CancellationTokenSource _run = new();
    private CancellationTokenSource _filmographyRun = new();
    private string? _refreshedFor;
    private Person? _shown;
    private bool _following;
    private bool _resetting;

    public PersonViewModel(ISessionManager session, CatalogApi catalog, AdminApi admin, ImageParameterCache images, AccountPermissions permissions,
        INavigationService navigation, TimeProvider time, ILogger<PersonViewModel> logger)
    {
        _session = session;
        _catalog = catalog;
        _admin = admin;
        _images = images;
        _permissions = permissions;
        _navigation = navigation;
        _time = time;
        _logger = logger;
        Filter = All;
    }

    /// <summary>All, Movies, Series, as the web's filmography buttons.</summary>
    public IReadOnlyList<SettingChoice> Filters { get; } = [All, new(MediaScopes.Movie, "Movies"), new(MediaScopes.Series, "Series")];

    [ObservableProperty]
    public partial SettingChoice Filter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(HasError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>"Couldn't load this person", or "This person isn't available" when it is gone.</summary>
    [ObservableProperty]
    public partial string ErrorTitle { get; private set; } = "";

    [ObservableProperty]
    public partial string ErrorText { get; private set; } = "";

    /// <summary>The read failed, not the person: Retry may find them.</summary>
    [ObservableProperty]
    public partial bool CanRetry { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    public partial string Name { get; private set; } = "";

    /// <summary>Absolute and presigned; null while there is none (the initials show).</summary>
    [ObservableProperty]
    public partial Uri? PhotoUrl { get; private set; }

    /// <summary>"Born Apr 25, 1940", "86 years old", the birthplace.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFacts))]
    public partial IReadOnlyList<string> Facts { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBio))]
    public partial string Bio { get; private set; } = "";

    /// <summary>The titles, in pages as the grid scrolls.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> Cards { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilmographyLoading), nameof(IsFilmographyEmpty), nameof(HasFilmographyError))]
    public partial LibraryLoadState FilmographyState { get; private set; }

    [ObservableProperty]
    public partial string FilmographyError { get; private set; } = "";

    /// <summary>A refresh's new details are being waited for.</summary>
    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

    /// <summary>Refresh was pressed and the server has not answered yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshLabel))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshPending { get; private set; }

    /// <summary>"Person refresh queued", "Person metadata refreshed", or why it failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefreshNotice))]
    public partial string RefreshNotice { get; private set; } = "";

    [ObservableProperty]
    public partial bool RefreshFailed { get; private set; }

    /// <summary>An administrator on the primary profile: the refresh waits for the provider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshLabel))]
    public partial bool IsActingAdmin { get; private set; }

    public string Initials => ProfileItemViewModel.InitialsOf(Name);

    public bool HasFacts => Facts.Count > 0;

    public bool HasBio => Bio.Length > 0;

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    public bool IsFilmographyLoading => FilmographyState == LibraryLoadState.Loading;

    /// <summary>The web's "No items found."</summary>
    public bool IsFilmographyEmpty => FilmographyState == LibraryLoadState.Empty;

    public bool HasFilmographyError => FilmographyState == LibraryLoadState.Error;

    public bool HasRefreshNotice => RefreshNotice.Length > 0;

    public string RefreshLabel => IsRefreshPending
        ? IsActingAdmin ? "Refreshing..." : "Queueing..."
        : IsActingAdmin ? "Refresh now" : "Refresh metadata";

    /// <summary>The display's scale, so posters are asked for at the pixels they are drawn at.</summary>
    public double DisplayScale { get; set; } = 1;

    /// <summary>The filmography's load in flight (for tests).</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Shows the person; Back to the same one keeps the page as it was.</summary>
    public async Task ShowAsync(string? personId, bool isReturn)
    {
        if (string.IsNullOrWhiteSpace(personId))
        {
            ShowGone();
            return;
        }
        if (isReturn && _shownFor == Key(personId) && State == LibraryLoadState.Ready)
        {
            return;
        }
        _personId = personId;
        StartOver();
        try
        {
            IsActingAdmin = await _permissions.IsActingAdminAsync();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            // Not knowing makes it a viewer's refresh, which the server takes from anyone.
            _logger.LogInformation("Could not tell whether the account acts as an administrator: {Failure}", ApiFailureText.Describe(ex));
        }
        await LoadAsync(personId);
    }

    [RelayCommand]
    private Task RetryAsync() => _personId is { } id ? LoadAsync(id) : Task.CompletedTask;

    /// <summary>For the grid while it scrolls.</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_pager is not { HasMore: true } pager || FilmographyState != LibraryLoadState.Ready)
        {
            return;
        }
        var token = _filmographyRun.Token;
        try
        {
            await pager.LoadNextPageAsync(token);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, token))
        {
            _logger.LogWarning("Loading more of a person's titles failed: {Failure}", ApiFailureText.Describe(ex));
            FilmographyError = BrowseFailureText.Describe(ex, "Couldn't load more titles.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another filter or person took over.
        }
    }

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null)
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    /// <summary>
    /// The web's Refresh metadata: queued for the provider, then read again until the details come. An administrator's
    /// Refresh now waits for the provider and shows what it gave.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_personId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var token = _run.Token;
        IsRefreshPending = true;
        RefreshNotice = "";
        RefreshFailed = false;
        try
        {
            if (IsActingAdmin)
            {
                var person = await _admin.RefreshPersonAsync(server.BaseUri, id, token);
                Show(person, server.BaseUri);
                RefreshNotice = "Person metadata refreshed";
            }
            else
            {
                await _catalog.RefreshPersonAsync(server.BaseUri, id, token);
                RefreshNotice = "Person refresh queued";
                _ = FollowRefreshAsync(server.BaseUri, id, token);
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, token))
        {
            _logger.LogWarning("Refreshing a person failed: {Failure}", ApiFailureText.Describe(ex));
            RefreshFailed = true;
            RefreshNotice = "Refresh failed. " + BrowseFailureText.Describe(ex, "Try again.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another person took over.
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsRefreshPending = false;
            }
        }
    }

    private bool CanRefresh() => !IsRefreshPending;

    partial void OnFilterChanged(SettingChoice value)
    {
        if (!_resetting && _personId is { } id && _session is { State: SessionState.Ready, ActiveServer: { } server } && State == LibraryLoadState.Ready)
        {
            Loading = LoadFilmographyAsync(server, id, value);
        }
    }

    private async Task LoadAsync(string id)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var token = _run.Token;
        State = LibraryLoadState.Loading;
        Person person;
        try
        {
            person = await _catalog.GetPersonAsync(server.BaseUri, id, token);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, token))
        {
            _logger.LogWarning("Loading a person failed: {Failure}", ApiFailureText.Describe(ex));
            if (ex is SiloApiException { Status: 404 })
            {
                ShowGone();
                return;
            }
            ErrorTitle = "Couldn't load this person";
            ErrorText = "Something went wrong while loading them. Try again in a moment.";
            CanRetry = true;
            State = LibraryLoadState.Error;
            return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another person took over.
            return;
        }
        if (token.IsCancellationRequested)
        {
            return;
        }
        Show(person, server.BaseUri);
        _shownFor = Key(id);
        State = LibraryLoadState.Ready;
        // Once per visit, as the apps: a person the provider told little about is refreshed and followed.
        if (person.IsIncomplete && _refreshedFor != id)
        {
            _refreshedFor = id;
            _ = QueueRefreshAsync(server.BaseUri, id, token);
        }
        Loading = LoadFilmographyAsync(server, id, Filter);
        await Loading;
    }

    private async Task LoadFilmographyAsync(ServerEntry server, string id, SettingChoice filter)
    {
        var previous = _filmographyRun;
        _filmographyRun = CancellationTokenSource.CreateLinkedTokenSource(_run.Token);
        previous.Cancel();
        previous.Dispose();
        var token = _filmographyRun.Token;
        FilmographyState = LibraryLoadState.Loading;
        FilmographyError = "";
        try
        {
            var images = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, token);
            var query = CatalogQuery.ForPerson(id) with
            {
                Type = filter.Value == All.Value ? null : filter.Value,
                Limit = PageSize,
                Images = images,
            };
            var pager = new BrowseCardPager(_catalog, server.BaseUri, query,
                item => item.ContentId.Length > 0 ? MediaCardFactory.Poster(item, server.BaseUri) : null);
            _pager = pager;
            Cards = pager.Cards;
            await pager.LoadFirstPageAsync(token);
            FilmographyState = pager.Cards.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) && !token.IsCancellationRequested)
        {
            _logger.LogWarning("Loading a person's titles failed: {Failure}", ApiFailureText.Describe(ex));
            FilmographyError = BrowseFailureText.Describe(ex, "Couldn't load the titles.");
            FilmographyState = LibraryLoadState.Error;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another filter or person took over.
        }
    }

    private async Task QueueRefreshAsync(Uri server, string id, CancellationToken token)
    {
        try
        {
            await _catalog.RefreshPersonAsync(server, id, token);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, token) || (ex is OperationCanceledException && token.IsCancellationRequested))
        {
            if (!token.IsCancellationRequested)
            {
                _logger.LogInformation("Queueing a person's refresh failed: {Failure}", ApiFailureText.Describe(ex));
            }
            return;
        }
        await FollowRefreshAsync(server, id, token);
    }

    // Reads the person again until the refresh shows, the window closes, or reads stop changing anything.
    private async Task FollowRefreshAsync(Uri server, string id, CancellationToken token)
    {
        if (_following)
        {
            return;
        }
        _following = true;
        IsRefreshing = true;
        long started = _time.GetTimestamp();
        var previous = Details(_shown);
        int unchanged = 0;
        try
        {
            for (int read = 0; read < MaxRefreshReads; read++)
            {
                await Task.Delay(RefreshPollInterval, _time, token);
                if (_time.GetElapsedTime(started) > RefreshWindow)
                {
                    return;
                }
                var person = await _catalog.GetPersonAsync(server, id, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
                var details = Details(person);
                if (details == previous)
                {
                    if (++unchanged >= MaxUnchangedReads)
                    {
                        return;
                    }
                    continue;
                }
                unchanged = 0;
                previous = details;
                Show(person, server);
                if (!person.IsIncomplete)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, token) || (ex is OperationCanceledException && token.IsCancellationRequested))
        {
            // A failed read ends the wait; the page keeps what it shows.
            if (!token.IsCancellationRequested)
            {
                _logger.LogInformation("Reading a refreshed person failed: {Failure}", ApiFailureText.Describe(ex));
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _following = false;
                IsRefreshing = false;
            }
        }
    }

    private void Show(Person person, Uri server)
    {
        _shown = person;
        Name = person.Name.Trim();
        PhotoUrl = ArtworkUrl.Resolve(server, person.PhotoUrl);
        var now = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone);
        Facts = PersonFacts.Of(person, DateOnly.FromDateTime(now.DateTime));
        Bio = person.Bio?.Trim() ?? "";
    }

    private void ShowGone()
    {
        ErrorTitle = "This person isn't available";
        ErrorText = "They may have been removed from the catalog, or the link may be wrong.";
        CanRetry = false;
        State = LibraryLoadState.Error;
    }

    // Another person (or the same anew): nothing of the last one stays, and its reads and refresh stop.
    private void StartOver()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        _following = false;
        _refreshedFor = null;
        _shown = null;
        _shownFor = null;
        _pager = null;
        _resetting = true;
        Filter = All;
        _resetting = false;
        Name = "";
        PhotoUrl = null;
        Facts = [];
        Bio = "";
        Cards = [];
        FilmographyState = LibraryLoadState.Idle;
        IsRefreshing = false;
        IsRefreshPending = false;
        RefreshNotice = "";
        RefreshFailed = false;
    }

    // What a refresh can change; the photo URL is signed anew on every read, so only whether there is one counts.
    private static (string, string?, string?, string?, string?, bool)? Details(Person? person) => person is null
        ? null
        : (person.Name, person.Bio, person.BirthDate, person.DeathDate, person.Birthplace, !string.IsNullOrWhiteSpace(person.PhotoUrl));

    private (string? ServerId, string? ProfileId, string PersonId) Key(string personId) => (_session.ActiveServer?.Id, _session.ProfileId, personId);
}
