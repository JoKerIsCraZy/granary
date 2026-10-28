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
/// few seconds until the details come. The reads stop while the page is not shown (<see cref="Pause"/>), and go on when
/// it is shown again, without another refresh.
/// </summary>
public sealed partial class PersonViewModel : ObservableObject
{
    public const int PageSize = 60;

    /// <summary>
    /// Reads after a refresh that change nothing before the page stops waiting for it: five, 3 s apart, as both apps
    /// (Android's <c>PersonDetailViewModel</c>, Apple's <c>metadataRefreshSettledPollCount</c>); a bound for the page,
    /// not proof the server's queue is done.
    /// </summary>
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
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<PersonViewModel> _logger;
    private (string? ServerId, string? ProfileId, string PersonId)? _shownFor;
    private string? _personId;
    private BrowseCardPager? _pager;
    // The page's reads while it is shown: Pause and another person cancel it.
    private CancellationTokenSource _run = new();
    private CancellationTokenSource _filmographyRun = new();
    private string? _refreshedFor;
    private Person? _shown;
    private bool _following;
    private bool _resetting;
    private bool _isActive;

    public PersonViewModel(ISessionManager session, CatalogApi catalog, AdminApi admin, ImageParameterCache images, AccountPermissions permissions,
        INavigationService navigation, IUiDispatcher dispatcher, TimeProvider time, ILogger<PersonViewModel> logger)
    {
        _session = session;
        _catalog = catalog;
        _admin = admin;
        _images = images;
        _permissions = permissions;
        _navigation = navigation;
        _dispatcher = dispatcher;
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

    /// <summary>Why the first page of titles did not come.</summary>
    [ObservableProperty]
    public partial string FilmographyError { get; private set; } = "";

    /// <summary>Why a later page did not come; the grid asks for no more until Try again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadMoreError))]
    public partial string LoadMoreError { get; private set; } = "";

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

    public bool HasLoadMoreError => LoadMoreError.Length > 0;

    public bool HasRefreshNotice => RefreshNotice.Length > 0;

    public string RefreshLabel => IsRefreshPending
        ? IsActingAdmin ? "Refreshing..." : "Queueing..."
        : IsActingAdmin ? "Refresh now" : "Refresh metadata";

    /// <summary>The display's scale, so posters are asked for at the pixels they are drawn at.</summary>
    public double DisplayScale { get; set; } = 1;

    /// <summary>The filmography's load in flight (for tests).</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The refresh reads in flight (for tests).</summary>
    internal Task Following { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Shows the person. Back to the same one keeps the page as it was, and goes on waiting for a refresh it was waiting
    /// for (without asking for another); anything else reads them anew.
    /// </summary>
    public async Task ShowAsync(string? personId, bool isReturn)
    {
        Activate();
        var token = NewRun();
        if (string.IsNullOrWhiteSpace(personId))
        {
            ShowGone();
            return;
        }
        bool keeps = isReturn && _shownFor == Key(personId) && State == LibraryLoadState.Ready;
        if (!keeps)
        {
            _personId = personId;
            StartOver();
        }
        // The account or profile may have changed since the page was last shown.
        await ReadAdminAsync(token);
        if (token.IsCancellationRequested)
        {
            return;
        }
        if (!keeps)
        {
            await LoadAsync(personId, token);
            return;
        }
        if (_session is { State: SessionState.Ready, ActiveServer: { } server } && _refreshedFor == personId && _shown is { IsIncomplete: true })
        {
            Following = FollowRefreshAsync(server.BaseUri, personId, token);
        }
    }

    /// <summary>
    /// The page is not shown: its reads stop, a refresh is no longer waited for, and the session is no longer followed.
    /// What it shows stays for Back.
    /// </summary>
    public void Pause()
    {
        _run.Cancel();
        _following = false;
        IsRefreshing = false;
        IsRefreshPending = false;
        if (_isActive)
        {
            _isActive = false;
            _session.Changed -= OnSessionChanged;
        }
    }

    /// <summary>Reads the person again, after a failure.</summary>
    [RelayCommand]
    private Task RetryAsync() => _personId is { } id ? LoadAsync(id, _run.Token) : Task.CompletedTask;

    /// <summary>Reads only the titles again, after their first page failed: the person stays.</summary>
    [RelayCommand]
    private Task RetryFilmographyAsync()
    {
        if (_personId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return Task.CompletedTask;
        }
        return Loading = LoadFilmographyAsync(server, id, Filter);
    }

    /// <summary>For the grid while it scrolls; after a failure only <see cref="LoadMoreAgainCommand"/> asks again.</summary>
    [RelayCommand]
    private Task LoadMoreAsync() => HasLoadMoreError ? Task.CompletedTask : LoadNextPageAsync();

    /// <summary>"Try again" under the grid after a later page failed.</summary>
    [RelayCommand]
    private Task LoadMoreAgainAsync()
    {
        LoadMoreError = "";
        return LoadNextPageAsync();
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
                Following = FollowRefreshAsync(server.BaseUri, id, token);
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
            // The page went, or another person took over.
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

    private async Task LoadAsync(string id, CancellationToken token)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            // Never a blank page: signed out or switching, the person cannot be read now.
            ShowFailure(LibraryErrors.Unreachable);
            return;
        }
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
            ShowFailure(BrowseFailureText.Describe(ex, "Something went wrong while loading them. Try again in a moment."));
            return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The page went, or another person took over.
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
            Following = QueueRefreshAsync(server.BaseUri, id, token);
        }
        Loading = LoadFilmographyAsync(server, id, Filter);
        await Loading;
    }

    private async Task LoadNextPageAsync()
    {
        if (_pager is not { HasMore: true } pager || FilmographyState != LibraryLoadState.Ready)
        {
            return;
        }
        var token = _filmographyRun.Token;
        try
        {
            await pager.LoadNextPageAsync(token);
            if (!token.IsCancellationRequested && ReferenceEquals(_pager, pager))
            {
                LoadMoreError = "";
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, token))
        {
            _logger.LogWarning("Loading more of a person's titles failed: {Failure}", ApiFailureText.Describe(ex));
            if (ReferenceEquals(_pager, pager))
            {
                LoadMoreError = BrowseFailureText.Describe(ex, "Couldn't load more titles.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another filter or person took over, or the page went.
        }
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
        LoadMoreError = "";
        BrowseCardPager? pager = null;
        try
        {
            var images = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, token);
            var query = CatalogQuery.ForPerson(id) with
            {
                Type = filter.Value == All.Value ? null : filter.Value,
                Limit = PageSize,
                Images = images,
            };
            pager = new BrowseCardPager(_catalog, server.BaseUri, query,
                item => item.ContentId.Length > 0 ? MediaCardFactory.Poster(item, server.BaseUri) : null);
            _pager = pager;
            Cards = pager.Cards;
            await pager.LoadFirstPageAsync(token);
            // A filter or person picked meanwhile owns the state now.
            if (token.IsCancellationRequested || !ReferenceEquals(_pager, pager))
            {
                return;
            }
            FilmographyState = pager.Cards.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another filter or person took over, or the page went.
        }
        catch (Exception ex) when (!token.IsCancellationRequested && (pager is null || ReferenceEquals(_pager, pager)))
        {
            // Whatever went wrong, the ring stops and Retry is offered.
            if (ApiFailureText.IsRequestFailure(ex))
            {
                _logger.LogWarning("Loading a person's titles failed: {Failure}", ApiFailureText.Describe(ex));
            }
            else
            {
                _logger.LogError(ex, "Loading a person's titles failed");
            }
            FilmographyError = BrowseFailureText.Describe(ex, "Couldn't load the titles.");
            FilmographyState = LibraryLoadState.Error;
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

    // Reads the person again until the refresh shows, the window closes, reads stop changing anything, or the page goes.
    private async Task FollowRefreshAsync(Uri server, string id, CancellationToken token)
    {
        if (_following || token.IsCancellationRequested)
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The page went, or another person took over.
        }
        catch (Exception ex)
        {
            // A failed read ends the wait; the page keeps what it shows.
            _logger.LogInformation("Reading a refreshed person failed: {Failure}", ApiFailureText.Describe(ex));
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

    private async Task ReadAdminAsync(CancellationToken token)
    {
        try
        {
            bool isAdmin = await _permissions.IsActingAdminAsync(token);
            if (!token.IsCancellationRequested)
            {
                IsActingAdmin = isAdmin;
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, token) || (ex is OperationCanceledException && token.IsCancellationRequested))
        {
            // Not knowing makes it a viewer's refresh, which the server takes from anyone.
            if (!token.IsCancellationRequested)
            {
                IsActingAdmin = false;
                _logger.LogInformation("Could not tell whether the account acts as an administrator: {Failure}", ApiFailureText.Describe(ex));
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

    private void ShowFailure(string why)
    {
        ErrorTitle = "Couldn't load this person";
        ErrorText = why;
        CanRetry = true;
        State = LibraryLoadState.Error;
    }

    // A new run for this showing: the last one's reads stop, and the filmography's follow this one.
    private CancellationToken NewRun()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var filmography = _filmographyRun;
        _filmographyRun = CancellationTokenSource.CreateLinkedTokenSource(_run.Token);
        filmography.Cancel();
        filmography.Dispose();
        _following = false;
        IsRefreshing = false;
        IsRefreshPending = false;
        return _run.Token;
    }

    // Another person (or the same anew): nothing of the last one stays.
    private void StartOver()
    {
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
        FilmographyError = "";
        LoadMoreError = "";
        RefreshNotice = "";
        RefreshFailed = false;
    }

    private void Activate()
    {
        if (!_isActive)
        {
            _isActive = true;
            _session.Changed += OnSessionChanged;
        }
    }

    // Another profile or server, or signed out: this one's reads stop. Changed can come from a background thread.
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            PauseForAnotherSession();
        }
        else
        {
            _dispatcher.Post(PauseForAnotherSession);
        }
    }

    private void PauseForAnotherSession()
    {
        if (_isActive && (_session.State != SessionState.Ready || (_personId is { } id && _shownFor is { } shown && shown != Key(id))))
        {
            Pause();
        }
    }

    // What a refresh can change; the photo URL is signed anew on every read, so only whether there is one counts.
    private static (string, string?, string?, string?, string?, bool)? Details(Person? person) => person is null
        ? null
        : (person.Name, person.Bio, person.BirthDate, person.DeathDate, person.Birthplace, !string.IsNullOrWhiteSpace(person.PhotoUrl));

    private (string? ServerId, string? ProfileId, string PersonId) Key(string personId) => (_session.ActiveServer?.Id, _session.ProfileId, personId);
}
