using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Settings;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>
/// "Start a Watch Party" in a title page's More menu and the sheet it opens, as the web's (<c>DetailWatchTogether.tsx</c>
/// and <c>pages/watchtogether/StartPartySheet.tsx</c>): how the room picks, whether guests may pause, and for a series
/// which episode. One press creates the room, applies the pause choice, puts the title up (staged for the host to start,
/// or the vote's first suggestion), copies the invite and lands in the room, nothing playing yet. Offered only while the
/// server hosts rooms that stage a title and this profile is in none, as Android's detail page offers hosting
/// (<c>WatchPartyDetailActions.kt</c>); a profile or server changed meanwhile ends it quietly, as the web's captured
/// authority does.
/// </summary>
public sealed partial class StartPartyViewModel : ObservableObject
{
    /// <summary>The profile's saved choice the server starts its next room with (the web's settings contract).</summary>
    public const string GuestControlKey = "watch_party.guest_control_policy";

    private readonly WatchPartySession _party;
    private readonly WatchPartyFeature _feature;
    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly SettingsApi _settings;
    private readonly INavigationService _navigation;
    private readonly ILogger<StartPartyViewModel> _logger;
    private ItemDetailViewModel? _page;
    // The saved choice once read (null for none), and whether it was.
    private string? _savedPolicy;
    private bool _savedKnown;
    // The host's own flip of the switch; null follows the saved choice.
    private bool? _guestsChoice;
    // Counts the openings: what an earlier one read is dropped.
    private int _opening;
    private CancellationTokenSource _reads = new();
    // The server and profile the sheet was opened for: it reads, and makes a room, for them only.
    private (string? ServerId, string? ProfileId) _openedFor;
    // The sheet was put away, or its page left, since it opened.
    private bool _dismissed;

    public StartPartyViewModel(WatchPartySession party, WatchPartyFeature feature, ISessionManager session, CatalogApi catalog, SettingsApi settings,
        INavigationService navigation, ILogger<StartPartyViewModel> logger)
    {
        _party = party;
        _feature = feature;
        _session = session;
        _catalog = catalog;
        _settings = settings;
        _navigation = navigation;
        _logger = logger;
    }

    /// <summary>The page's More menu offers it: the page has a title, the server hosts rooms, and this profile is in none.</summary>
    [ObservableProperty]
    public partial bool IsOffered { get; private set; }

    /// <summary>The page the sheet was opened for; null before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Caption), nameof(HasCaption), nameof(Poster), nameof(PosterThumbhash), nameof(HasEpisodeChoice),
        nameof(ShowsPick))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial StartPartyRequest? Request { get; private set; }

    public string Title => Request?.Title ?? "";

    public string Caption => Request?.Caption ?? "";

    public bool HasCaption => Caption.Length > 0;

    public Uri? Poster => Request?.Poster;

    public string PosterThumbhash => Request?.PosterThumbhash ?? "";

    /// <summary>What the party puts up first; null when nothing is picked (the room then opens empty).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeText), nameof(ChangeLabel), nameof(ChangeName), nameof(HostPickCaption), nameof(VoteCaption))]
    public partial StartPartyTarget? Target { get; private set; }

    /// <summary>A series' next episode is being named.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EpisodeText))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsNamingTarget { get; private set; }

    /// <summary>The Episode row shows: the page is a series, a season or an episode.</summary>
    public bool HasEpisodeChoice => Request?.SeriesId is not null;

    /// <summary><c>S3 E1 "Chapter 1"</c>, or "Choose an episode".</summary>
    public string EpisodeText => Target?.EpisodeText ?? (IsNamingTarget ? "" : AppText.Get("web.watchTogether.startSheet.chooseEpisode"));

    public string ChangeLabel => Target is null ? AppText.Get("web.watchTogether.startSheet.choose") : AppText.Get("web.watchTogether.common.change");

    /// <summary>What the Change button changes, for a screen reader: "Choose an episode" or "Change the episode".</summary>
    public string ChangeName => Target is null ? AppText.Get("web.watchTogether.startSheet.chooseEpisode") : AppText.Get("granary.watchParty.changeEpisode");

    /// <summary>How the room picks: <see cref="WatchPartySelectionModes.HostPick"/> first on every opening, as on the web.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostPick), nameof(IsVoteMode))]
    public partial string SelectionMode { get; private set; } = WatchPartySelectionModes.HostPick;

    /// <summary>The Host picks card is chosen; choosing it is a radio's check (an uncheck says nothing).</summary>
    public bool IsHostPick
    {
        get => SelectionMode == WatchPartySelectionModes.HostPick;
        set
        {
            if (value)
            {
                SelectionMode = WatchPartySelectionModes.HostPick;
            }
        }
    }

    /// <summary>The Everyone votes card is chosen.</summary>
    public bool IsVoteMode
    {
        get => SelectionMode == WatchPartySelectionModes.Vote;
        set
        {
            if (value)
            {
                SelectionMode = WatchPartySelectionModes.Vote;
            }
        }
    }

    public string HostPickCaption => Target is null
        ? AppText.Get("web.watchTogether.startSheet.hostPickCaptionEmpty")
        : AppText.Get("web.watchTogether.startSheet.hostPickCaptionTarget");

    public string VoteCaption => Target is null
        ? AppText.Get("web.watchTogether.startSheet.voteCaptionEmpty")
        : AppText.Get("web.watchTogether.startSheet.voteCaptionTarget");

    /// <summary>
    /// "Allow guests to pause": the profile's saved choice, which the server gives the new room, until the host flips it.
    /// </summary>
    public bool GuestsCanPause
    {
        get => _guestsChoice ?? _savedPolicy == WatchPartyPolicies.GuestPlayPause;
        set
        {
            // The switch echoing what it was shown is not the host's choice.
            if (value != GuestsCanPause)
            {
                _guestsChoice = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The saved choice is being read: the switch may still change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsReadingSaved { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateLabel))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsBusy { get; private set; }

    public string CreateLabel => IsBusy ? AppText.Get("web.watchTogether.startSheet.creating") : AppText.Get("web.watchTogether.startSheet.create");

    /// <summary>Why the room could not be made, in the server's words where it has some.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = "";

    public bool HasError => Error.Length > 0;

    /// <summary>The sheet is done: the room opened, or the profile it was opened for is gone.</summary>
    [ObservableProperty]
    public partial bool IsDone { get; private set; }

    /// <summary>Puts the invite link on the clipboard, which the page has; false when it could not.</summary>
    public Func<string, bool> CopyText { get; set; } = static _ => false;

    /// <summary>The reads an opening started (the saved choice, a series' next episode), for tests.</summary>
    internal Task Opening { get; private set; } = Task.CompletedTask;

    /// <summary>Follows <paramref name="page"/>, the server's rooms and this profile's room, while the page shows.</summary>
    public void Attach(ItemDetailViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (ReferenceEquals(_page, page))
        {
            return;
        }
        Detach();
        _page = page;
        page.PropertyChanged += OnPageChanged;
        _feature.PropertyChanged += OnRoomsChanged;
        _party.PropertyChanged += OnRoomsChanged;
        ShowOffered();
        Refreshing = RefreshRoomsAsync();
    }

    /// <summary>The read of the server's rooms the last attach started, for tests.</summary>
    internal Task Refreshing { get; private set; } = Task.CompletedTask;

    // Read once per profile; the sidebar has usually read it already. Nobody waits for it, so nothing may escape: a
    // failure of any kind only leaves the entry away.
    private async Task RefreshRoomsAsync()
    {
        try
        {
            await _feature.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read whether the server hosts Watch Party");
        }
    }

    /// <summary>The page left (a profile picked, a sign-out): a sheet still open for it closes.</summary>
    public void Detach()
    {
        if (_page is not { } page)
        {
            return;
        }
        _page = null;
        page.PropertyChanged -= OnPageChanged;
        _feature.PropertyChanged -= OnRoomsChanged;
        _party.PropertyChanged -= OnRoomsChanged;
        IsOffered = false;
        if (Request is not null && !IsDone)
        {
            Close();
            IsDone = true;
        }
    }

    /// <summary>Opens the sheet for the page: false when it offers no party, or a room it asked for is still on its way.</summary>
    public bool Open()
    {
        if (!IsOffered || IsBusy || _page?.WatchPartyRequest() is not { } request)
        {
            return false;
        }
        int opening = ++_opening;
        _openedFor = (_session.ActiveServer?.Id, _session.ProfileId);
        _dismissed = false;
        _reads.Cancel();
        _reads.Dispose();
        _reads = new CancellationTokenSource();
        var token = _reads.Token;
        Request = request;
        Target = request.Target;
        SelectionMode = WatchPartySelectionModes.HostPick;
        _guestsChoice = null;
        _savedPolicy = null;
        _savedKnown = false;
        OnPropertyChanged(nameof(GuestsCanPause));
        Error = "";
        IsDone = false;
        ResetChooser();
        IsNamingTarget = request.Target is null && request.NextUpId is not null;
        Opening = Task.WhenAll(ReadSavedAsync(opening, token), NameNextUpAsync(request, opening, token));
        return true;
    }

    /// <summary>
    /// The sheet closed (Cancel, or the page left): its reads stop. A room on its way is still entered and set up, as the
    /// web's is, but the invite is not copied and the room page does not open over wherever the viewer went.
    /// </summary>
    public void Close()
    {
        _dismissed = true;
        _opening++;
        _reads.Cancel();
        IsReadingSaved = false;
        IsNamingTarget = false;
        ResetChooser();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        // One room per press: a second Create while one is on its way makes none.
        if (IsBusy || Request is not { } request)
        {
            return;
        }
        // Everything is for the server and profile the sheet was opened for: another one's sheet makes nothing.
        var owner = _openedFor;
        if (!Same(owner))
        {
            IsDone = true;
            return;
        }
        IsBusy = true;
        Error = "";
        try
        {
            var plan = new StartPartyPlan(request, Target, SelectionMode,
                (GuestsCanPause, Explicit: _guestsChoice is not null, Known: _savedKnown, Saved: _savedPolicy), owner);
            if (await MakeRoomAsync(plan.Mode) is not { } room)
            {
                return;
            }
            var notices = await SetUpAsync(room, plan);
            // Decided before the sheet is done, which closes it: the room opens only over the sheet that asked for it.
            bool opens = notices is not null && Same(owner) && _party.Room?.RoomId == room.RoomId && !_dismissed;
            // The room was entered: the sheet is done whatever comes next.
            IsDone = true;
            if (opens && notices is not null)
            {
                OpenRoom(notices);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // What the press asked for, as the sheet showed it then.
    private sealed record StartPartyPlan(StartPartyRequest Request, StartPartyTarget? Target, string Mode,
        (bool GuestsCanPause, bool Explicit, bool Known, string? Saved) Choice, (string? ServerId, string? ProfileId) Owner);

    // The room, or null with the reason said (in the server's words where it has some). No token: a room asked for is not
    // called off. Cancel leaves it to finish, as the web's sheet does, and the session enters it rather than leave a room
    // waiting for a host who never comes.
    private async Task<WatchPartyRoom?> MakeRoomAsync(string mode)
    {
        try
        {
            return await _party.CreateAsync(mode);
        }
        catch (WatchPartyStaleException)
        {
            // The profile or server changed while the room was made: it is not entered, and the sheet is not theirs.
            _logger.LogDebug("A Watch Party started for the profile before was not entered");
            IsDone = true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not start a Watch Party from a title");
            Error = ex is SiloApiException { Problem.Detail: { Length: > 0 } detail } ? detail : AppText.Get("web.watchTogether.startSheet.createFailed");
        }
        catch (Exception ex)
        {
            // A command's failure would end the app: anything else is said too.
            _logger.LogError(ex, "Starting a Watch Party from a title failed unexpectedly");
            Error = AppText.Get("web.watchTogether.startSheet.createFailed");
        }
        return null;
    }

    // The pause choice, the title up (staged or suggested, then the room named after it), and the invite copied: what the
    // room page says of it, or null when the server or profile changed meanwhile. A step that fails says so and the next
    // goes on; one that fails unexpectedly is logged and the room opens all the same.
    private async Task<List<string>?> SetUpAsync(WatchPartyRoom room, StartPartyPlan plan)
    {
        var notices = new List<string>();
        try
        {
            if (!Same(plan.Owner))
            {
                return null;
            }
            await ApplyPolicyAsync(room, plan.Choice, notices);
            if (!Same(plan.Owner))
            {
                return null;
            }
            if (plan.Target is { } target
                && await (plan.Mode == WatchPartySelectionModes.Vote ? SuggestAsync(target, plan.Request, notices) : StageAsync(target, notices)))
            {
                _party.NoteTitle(target.RoomTitle);
            }
            if (!Same(plan.Owner))
            {
                return null;
            }
            // A sheet put away meanwhile copies nothing: the viewer moved on.
            if (!_dismissed && CopyInvite() is { } copied)
            {
                notices.Add(copied);
            }
        }
        catch (WatchPartyStaleException)
        {
            _logger.LogDebug("A Watch Party started for the profile before was not set up");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A new Watch Party could not be set up as the sheet asked");
        }
        return notices;
    }

    // The room page, with what happened on the way; one that cannot open leaves the viewer in the room all the same.
    private void OpenRoom(List<string> notices)
    {
        try
        {
            _navigation.NavigateTo(AppRoute.WatchPartyRoom, notices.Count > 0 ? new WatchPartyArrival(string.Join("\n", notices)) : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The new Watch Party's room could not be opened");
        }
    }

    // Not while the episode is being chosen or named, nor before the switch shows the saved choice, as the web's button
    // waits; not in a room either, which a new one would leave.
    private bool CanCreate() => Request is not null && !IsBusy && !IsChoosing && !IsReadingSaved && !IsNamingTarget && !_party.IsInRoom;

    private bool Same((string? ServerId, string? ProfileId) owner) => (_session.ActiveServer?.Id, _session.ProfileId) == owner;

    // The room as the switch showed it, as the web's sheet: an untouched switch whose saved choice was not read leaves the
    // room's and says what it is; otherwise the room is changed where it differs, and a flipped switch is sent even where it
    // does not, since the server keeps the host's choice for the next party (guest_control_preference.go).
    private async Task ApplyPolicyAsync(WatchPartyRoom room, (bool GuestsCanPause, bool Explicit, bool Known, string? Saved) choice, List<string> notices)
    {
        string policy = choice.GuestsCanPause ? WatchPartyPolicies.GuestPlayPause : WatchPartyPolicies.HostOnly;
        if (!choice.Explicit && !choice.Known)
        {
            if (room.GuestControlPolicy != policy)
            {
                notices.Add(room.GuestsCanPause
                    ? AppText.Get("web.watchTogether.startSheet.policyGuestsCanPause")
                    : AppText.Get("web.watchTogether.startSheet.policyHostOnly"));
            }
            return;
        }
        bool alreadySaved = choice.Known && choice.Saved == policy;
        if (room.GuestControlPolicy == policy && !(choice.Explicit && !alreadySaved))
        {
            return;
        }
        string notApplied = choice.GuestsCanPause
            ? AppText.Get("web.watchTogether.startSheet.policyFailed")
            : AppText.Get("web.watchTogether.startSheet.policyHostOnlyFailed");
        try
        {
            if (await _party.SetGuestsCanPauseAsync(choice.GuestsCanPause) && _party.Room?.GuestControlPolicy != policy)
            {
                notices.Add(notApplied);
            }
        }
        catch (WatchPartyStaleException)
        {
            // Not a refusal: the room is no longer this profile's, and nothing is said of it.
            throw;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "The new Watch Party's pause choice could not be applied");
            notices.Add(notApplied);
        }
    }

    // A host-pick room stages the title: up next, waiting for the host's start. True when it went through.
    private async Task<bool> StageAsync(StartPartyTarget target, List<string> notices)
    {
        try
        {
            await _party.StageAsync(target.ContentId);
            return true;
        }
        catch (WatchPartyStaleException)
        {
            throw;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "The new Watch Party's title could not be staged");
            notices.Add(AppText.Get("web.watchTogether.startSheet.stageFailed"));
            return false;
        }
    }

    // A vote room gets it as its first suggestion, which the web's room page adds on arrival; this device has the room's
    // proof already, so it is added before the room shows. True when it went through.
    private async Task<bool> SuggestAsync(StartPartyTarget target, StartPartyRequest request, List<string> notices)
    {
        try
        {
            await _party.SuggestAsync(new WatchPartySuggestionCreate(Guid.NewGuid().ToString(), target.ContentId, target.ContentType, target.Title,
                target.SuggestionSubtitle, request.PosterPath));
            return true;
        }
        catch (WatchPartyStaleException)
        {
            throw;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "The new Watch Party's first suggestion could not be added");
            notices.Add(ex is SiloApiException { Problem.Detail: { Length: > 0 } detail } ? detail : AppText.Get("web.watchTogether.room.firstSuggestionFailed"));
            return false;
        }
    }

    // The web's copyWatchTogetherInvite: nothing said while the room has no invite yet.
    private string? CopyInvite()
    {
        if (_party.InviteLink is not { } link)
        {
            return null;
        }
        return CopyText(link.AbsoluteUri)
            ? WatchPartyText.InviteCopied(_party.Room?.Code)
            : AppText.Get("web.watchTogether.actions.inviteCopyFailed");
    }

    private async Task ReadSavedAsync(int opening, CancellationToken token)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        IsReadingSaved = true;
        try
        {
            var settings = await _settings.GetEffectiveAsync(server.BaseUri, [GuestControlKey], token);
            if (opening != _opening || !Same(_openedFor))
            {
                return;
            }
            _savedPolicy = settings.Find(GuestControlKey) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
            _savedKnown = true;
            OnPropertyChanged(nameof(GuestsCanPause));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The sheet closed or opened again.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            // An older server, or a failed read: the room keeps what the server gives it, and the create says what that is.
            _logger.LogWarning("The saved Watch Party pause choice could not be read: {Failure}", ex.GetType().Name);
        }
        finally
        {
            if (opening == _opening)
            {
                IsReadingSaved = false;
            }
        }
    }

    // A series' page knows its next episode by its id: the sheet names it from its own read, as the web's SeriesContent.tsx
    // finds it in its season's list.
    private async Task NameNextUpAsync(StartPartyRequest request, int opening, CancellationToken token)
    {
        if (request.Target is not null || request.NextUpId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            // Nothing to read: the sheet asks for an episode instead of waiting for one.
            if (opening == _opening)
            {
                IsNamingTarget = false;
            }
            return;
        }
        try
        {
            var episode = await _catalog.GetItemAsync(server.BaseUri, id, cancellationToken: token);
            if (opening == _opening && Same(_openedFor) && Target is null && episode is { Type: CatalogItemTypes.Episode, Versions.Count: > 0 })
            {
                string code = episode.SeasonNumber is { } season && episode.EpisodeNumber is { } number ? StartPartyTarget.EpisodeCode(season, number) : "";
                Target = new StartPartyTarget(id, CatalogItemTypes.Episode, episode.Title, code, request.SeriesTitle);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The sheet closed or opened again.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is ArgumentException)
        {
            // Nothing is picked then: the viewer chooses one, or the room opens empty.
            _logger.LogWarning("The series' next episode could not be read: {Failure}", ex.GetType().Name);
        }
        finally
        {
            if (opening == _opening)
            {
                IsNamingTarget = false;
            }
        }
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemDetailViewModel.OffersWatchParty))
        {
            ShowOffered();
        }
    }

    private void OnRoomsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WatchPartyFeature.Capabilities) or nameof(WatchPartyFeature.IsAvailable) or nameof(WatchPartySession.Room)
            or nameof(WatchPartySession.End))
        {
            ShowOffered();
            CreateCommand.NotifyCanExecuteChanged();
        }
    }

    // The server hosts rooms whose lobby stages a title (Android's watchPartyHostingOffered), and this profile is in none:
    // a new room would leave the one it is in, which the apps never offer from a title's page.
    private void ShowOffered() => IsOffered = _page?.OffersWatchParty == true
        && _feature.Capabilities is { IsAvailable: true, StagedSelection: true }
        && !_party.IsInRoom;
}
