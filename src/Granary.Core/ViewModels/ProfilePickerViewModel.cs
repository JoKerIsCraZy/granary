using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Profiles;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// "Who is watching?": lists the account's profiles and selects one, asking for the PIN of a protected
/// profile first (docs/api-contract.md, 6.1-6.3). The shell navigates once the session has a profile.
/// The PIN is only a command parameter: it is never stored or logged.
/// </summary>
/// <remarks>
/// The commands take no cancellation token on purpose: a cancellable toolkit command cancels its running
/// invocation when it is executed again (a double click, a reopened prompt). Stale answers are dropped
/// through the load and PIN generations instead.
/// </remarks>
public sealed partial class ProfilePickerViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Silo PINs are four digits: the web, Android and Apple clients only ever set such PINs and enter them
    /// on a four-digit pad. Anything else cannot be right and must not cost one of the five attempts.
    /// </summary>
    public const int PinLength = 4;

    internal static string WrongPin => AppText.Get("web.profiles.pin.incorrect");

    private readonly ProfilesApi _profiles;
    private readonly ISessionManager _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ProfilePickerViewModel> _logger;

    // A newer load, or a cancelled or replaced PIN prompt, makes the answers of older calls stale.
    private int _loadGeneration;
    private int _pinGeneration;

    // The server the last load asked (null: none was active), and the server the shown list came from.
    private bool _hasLoaded;
    private string? _requestedServerId;
    private string? _requestedAccountId;
    private string? _listServerId;
    private bool _disposed;

    // The household the shown list came from and the account it was read for: Add profile writes as that account's primary
    // profile on that server, never as whichever is signed in by then.
    private ProfileCollection? _household;
    private string? _listAccountId;

    // The open PIN prompt is the primary profile's, for Add profile rather than for picking it.
    private bool _pinForAdding;

    public ProfilePickerViewModel(ProfilesApi profiles, ISessionManager session, IUiDispatcher dispatcher, ILogger<ProfilePickerViewModel> logger)
    {
        _profiles = profiles;
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
        _session.Changed += OnSessionChanged;
    }

    [ObservableProperty]
    public partial IReadOnlyList<ProfileItemViewModel> Profiles { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsSelecting { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>The list could not be loaded; the page offers <see cref="RetryCommand"/>.</summary>
    [ObservableProperty]
    public partial bool HasLoadError { get; private set; }

    /// <summary>The PIN-protected profile the open prompt is for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinPromptTitle))]
    public partial ProfileItemViewModel? PinProfile { get; private set; }

    [ObservableProperty]
    public partial bool IsPinPromptOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsVerifyingPin { get; private set; }

    [ObservableProperty]
    public partial string PinError { get; private set; } = "";

    // Without a profile only while the dialog closes.
    public string PinPromptTitle => PinProfile is { } profile
        ? AppText.Get("web.profiles.pin.title", ("name", profile.Name))
        : AppText.Get("granary.profiles.pin.titleNoName");

    /// <summary>The line under the PIN prompt's title: why the primary profile's PIN is asked for Add profile, else nothing.</summary>
    [ObservableProperty]
    public partial string PinPromptDescription { get; private set; } = "";

    /// <summary>Add profile shows: the household has a primary profile to manage it, and room, as the apps offer it.</summary>
    [ObservableProperty]
    public partial bool CanAddProfile { get; private set; }

    /// <summary>The editor Add profile opened; the page shows it while it is set and closes it through <see cref="CloseEditorAsync"/>.</summary>
    [ObservableProperty]
    public partial ProfileEditorViewModel? Editor { get; private set; }

    public static bool IsCompletePin([NotNullWhen(true)] string? pin) => pin is { Length: PinLength } && pin.All(char.IsAsciiDigit);

    /// <summary>
    /// Add profile, as the apps' picker: the household's primary profile writes it without being picked, so the person still
    /// picks who is watching. Its PIN is asked first when it has one (Android's <c>requestAddProfile</c>).
    /// </summary>
    [RelayCommand]
    private void AddProfile()
    {
        if (IsSelecting || Editor is not null || !CanAddProfile)
        {
            return;
        }
        ClosePinPrompt();
        if (!ListBelongsToActiveServer(out _) || _household is null)
        {
            LoadCommand.Execute(null);
            return;
        }
        if (_household.Items.FirstOrDefault(p => p.IsPrimary) is not { } primary)
        {
            Status = AppText.Get("web.profiles.picker.primaryMissing");
            return;
        }
        if (primary.HasPin)
        {
            PinProfile = Profiles.FirstOrDefault(p => p.Id == primary.Id);
            _pinForAdding = PinProfile is not null;
            PinPromptDescription = AppText.Get("web.profiles.picker.addNeedsPin");
            IsPinPromptOpen = _pinForAdding;
            return;
        }
        OpenEditor(primary.Id, null);
    }

    /// <summary>The editor closed: it and the primary profile's proof go; a saved profile shows in the list read again.</summary>
    public async Task CloseEditorAsync(ProfileEditorSaved? saved)
    {
        Editor = null;
        if (saved is not null)
        {
            await LoadProfilesAsync();
        }
    }

    // The proof lives in the editor's target alone, for this one Add.
    private void OpenEditor(string primaryId, string? token)
    {
        if (!ListBelongsToActiveServer(out var server) || _household is not { } household || _listAccountId is not { } accountId
            || _session.Account?.Id != accountId)
        {
            Status = AppText.Get("web.profiles.picker.addFormFailed");
            return;
        }
        ManagingProfile lent;
        try
        {
            lent = ManagingProfile.Lent(server.BaseUri, accountId, primaryId, token);
        }
        catch (HttpRequestException)
        {
            // A proof the server sent that cannot go back as a header.
            _logger.LogWarning("The primary profile's PIN check answered with a proof that cannot be sent");
            Status = AppText.Get("web.profiles.picker.addFormFailed");
            return;
        }
        Editor = new ProfileEditorViewModel(_profiles, new ProfileEditorTarget(server.BaseUri, lent, household), null, _logger);
    }

    [RelayCommand]
    private Task LoadAsync() => LoadProfilesAsync();

    [RelayCommand]
    private Task RetryAsync() => LoadProfilesAsync();

    /// <summary>Selects a profile without a PIN, or opens the PIN prompt for a protected one.</summary>
    [RelayCommand]
    private async Task SelectAsync(ProfileItemViewModel? profile)
    {
        // A click can arrive after a reload replaced the list it came from.
        if (profile is null || IsSelecting || !Profiles.Any(p => p.Id == profile.Id))
        {
            return;
        }
        ClosePinPrompt();
        if (!ListBelongsToActiveServer(out var server))
        {
            LoadCommand.Execute(null);
            return;
        }
        if (profile.HasPin)
        {
            PinProfile = profile;
            PinPromptDescription = "";
            IsPinPromptOpen = true;
            return;
        }
        await CommitAsync(server.Id, profile.Id, null);
    }

    /// <summary>
    /// Checks the PIN of <see cref="PinProfile"/> once. verify-pin counts every attempt toward a lockout,
    /// so a failure is reported and never retried automatically.
    /// </summary>
    [RelayCommand]
    private async Task SubmitPinAsync(string? pin)
    {
        if (PinProfile is not { } profile || !IsPinPromptOpen || IsVerifyingPin)
        {
            return;
        }
        if (!IsCompletePin(pin))
        {
            PinError = AppText.Get("granary.profiles.pin.allDigits", ("count", PinLength));
            return;
        }
        if (!ListBelongsToActiveServer(out var server))
        {
            ClosePinPrompt();
            LoadCommand.Execute(null);
            return;
        }

        int generation = ++_pinGeneration;
        IsVerifyingPin = true;
        PinError = "";
        PinOutcome outcome;
        try
        {
            outcome = await VerifyPinAsync(server, profile.Id, pin);
        }
        finally
        {
            if (generation == _pinGeneration)
            {
                IsVerifyingPin = false;
            }
        }
        if (generation != _pinGeneration)
        {
            // The prompt was cancelled or replaced while the server checked the PIN.
            return;
        }
        if (_session.ActiveServer?.Id != server.Id)
        {
            // Another server became active meanwhile: this token must not be applied to it.
            ClosePinPrompt();
            return;
        }

        if (outcome.ProfileToken is { } token)
        {
            bool forAdding = _pinForAdding;
            ClosePinPrompt();
            if (forAdding)
            {
                OpenEditor(profile.Id, token);
                return;
            }
            await CommitAsync(server.Id, profile.Id, token);
        }
        else if (outcome.ProfileChanged)
        {
            // The profile lost its PIN or is gone: show the current list.
            ClosePinPrompt();
            await LoadProfilesAsync();
            if (!HasLoadError && Profiles.Count > 0)
            {
                Status = AppText.Get("granary.profiles.changed");
            }
        }
        else
        {
            PinError = outcome.Error ?? WrongPin;
        }
    }

    [RelayCommand]
    private void CancelPin() => ClosePinPrompt();

    private async Task LoadProfilesAsync()
    {
        int generation = ++_loadGeneration;
        var server = _session.ActiveServer;
        _hasLoaded = true;
        _requestedServerId = server?.Id;
        _requestedAccountId = _session.Account?.Id;
        if (server is null)
        {
            IsLoading = false;
            ShowList([], null);
            SetStatus(AppText.Get("granary.profiles.noServer"), isLoadError: false);
            return;
        }

        IsLoading = true;
        SetStatus("", isLoadError: false);
        string? accountId = _session.Account?.Id;
        try
        {
            var collection = await _profiles.ListAsync(server.BaseUri);
            if (generation != _loadGeneration)
            {
                return;
            }
            ShowList([.. collection.Items.Select(p => ProfileItemViewModel.From(p, server.BaseUri))], server.Id, collection, accountId);
            if (Profiles.Count == 0)
            {
                SetStatus(AppText.Get("granary.profiles.empty"), isLoadError: false);
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            if (generation != _loadGeneration)
            {
                return;
            }
            _logger.LogWarning("Listing profiles failed: {Failure}", ApiFailureText.Describe(ex));
            if (_listServerId != server.Id)
            {
                ShowList([], null);
            }
            SetStatus(DescribeListFailure(ex), isLoadError: true);
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    private async Task<PinOutcome> VerifyPinAsync(ServerEntry server, string profileId, string pin)
    {
        try
        {
            var verification = await _profiles.VerifyPinAsync(server.BaseUri, profileId, pin);
            if (!verification.Valid)
            {
                return new PinOutcome(null, WrongPin, false);
            }
            if (string.IsNullOrEmpty(verification.ProfileToken))
            {
                _logger.LogWarning("A PIN check was valid but carried no profile token");
                return new PinOutcome(null, AppText.Get("granary.profiles.pin.unconfirmed"), false);
            }
            return new PinOutcome(verification.ProfileToken, null, false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("A PIN check failed: {Failure}", ApiFailureText.Describe(ex));
            return ex is SiloApiException { Status: 404 }
                ? new PinOutcome(null, null, true)
                : new PinOutcome(null, DescribePinFailure(ex), false);
        }
    }

    // The pick goes to the server the list came from; when another one became active meanwhile it is dropped, and the
    // list follows the new server (OnSessionChanged).
    private async Task CommitAsync(string serverId, string profileId, string? profileToken)
    {
        IsSelecting = true;
        try
        {
            await _session.SelectProfileAsync(serverId, profileId, profileToken);
        }
        catch (ActiveServerChangedException)
        {
            _logger.LogInformation("Another server became active while a profile was picked; the pick was dropped");
            // The session may have moved on before this list was shown: the new server's profiles come either way.
            if (!ListBelongsToActiveServer(out _))
            {
                LoadCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Selecting a profile failed: {Failure}", ApiFailureText.Describe(ex));
            SetStatus(AppText.Get("granary.profiles.openFailed"), isLoadError: false);
        }
        finally
        {
            IsSelecting = false;
        }
    }

    // A selection is only valid on the server the shown list came from.
    private bool ListBelongsToActiveServer([NotNullWhen(true)] out ServerEntry? server)
    {
        server = _session.ActiveServer;
        return server is not null && server.Id == _listServerId;
    }

    private void ShowList(IReadOnlyList<ProfileItemViewModel> profiles, string? serverId, ProfileCollection? household = null, string? accountId = null)
    {
        Profiles = profiles;
        _listServerId = serverId;
        _household = household;
        _listAccountId = accountId;
        CanAddProfile = household is { HasRoom: true } && accountId is not null && household.Items.Any(p => p.IsPrimary);
    }

    private void SetStatus(string text, bool isLoadError)
    {
        Status = text;
        HasLoadError = isLoadError;
    }

    private void ClosePinPrompt()
    {
        _pinGeneration++;
        IsPinPromptOpen = false;
        IsVerifyingPin = false;
        PinError = "";
        PinProfile = null;
        _pinForAdding = false;
    }

    // Another server became active (or none): the shown list, a pending load and an open prompt belong
    // to the old one.
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        // Another account on the same server counts as another server: its profiles, PINs and proofs are not the old ones.
        if (_disposed || !_hasLoaded || (_session.ActiveServer?.Id == _requestedServerId && _session.Account?.Id == _requestedAccountId))
        {
            return;
        }
        _loadGeneration++;
        IsLoading = false;
        ClosePinPrompt();
        // An open Add belongs to the old server, and so does its proof.
        Editor = null;
        ShowList([], null);
        SetStatus("", isLoadError: false);
        _requestedServerId = _session.ActiveServer?.Id;
        _requestedAccountId = _session.Account?.Id;
        if (_session.State == SessionState.ProfileRequired)
        {
            LoadCommand.Execute(null);
        }
    });

    private static string DescribeListFailure(Exception ex) => ex switch
    {
        SiloApiException { Status: 429 } api => ApiFailureText.TooManyRequests(api.RetryAfter),
        SiloApiException { Status: 503 } => ApiFailureText.Busy,
        SiloApiException api => AppText.Get("granary.profiles.listFailed", ("status", api.Status)),
        _ => ApiFailureText.ForTransport(ex),
    };

    private static string DescribePinFailure(Exception ex) => ex switch
    {
        SiloApiException { Status: 429 } api => PinLockout(api.RetryAfter),
        SiloApiException { Status: 503 } => ApiFailureText.Busy,
        SiloApiException api => AppText.Get("granary.profiles.pin.checkFailed", ("status", api.Status)),
        _ => ApiFailureText.ForTransport(ex),
    };

    // A PIN check refused with 429 locks the profile for a while, in the web's words (components/profiles/pinLockout.ts).
    private static string PinLockout(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } delay)
        {
            return AppText.Get("web.profiles.pin.lockedLater");
        }
        int minutes = ApiFailureText.WholeMinutes(delay);
        return AppText.Get("web.profiles.pin.lockedMinutes", ("count", minutes), ("count_text", AppText.Number(minutes)));
    }

    public void Dispose()
    {
        _disposed = true;
        _session.Changed -= OnSessionChanged;
        // Add profile's editor holds the primary profile's proof: it goes with the picker, whatever its dialog still does.
        Editor = null;
    }

    private readonly record struct PinOutcome(string? ProfileToken, string? Error, bool ProfileChanged);
}

/// <summary>
/// User-facing wording and log-safe summaries for failed API calls in the sign-in view models; Home, detail, the player and
/// the password change use the wording too.
/// </summary>
internal static class ApiFailureText
{
    public static string Busy => AppText.Get("granary.apiFailure.busy");

    public static string Unreachable => AppText.Get("granary.apiFailure.unreachable");

    public static string Unexpected => AppText.Get("granary.apiFailure.unexpected");

    /// <summary>
    /// "The server is receiving too many requests. Try again in N minutes." as one message, from a <c>Retry-After</c>
    /// delay rounded up to whole minutes.
    /// </summary>
    public static string TooManyRequests(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } delay)
        {
            return AppText.Get("granary.apiFailure.tooManyRequestsLater");
        }
        return AppText.Get("granary.apiFailure.tooManyRequests", ("count", WholeMinutes(delay)));
    }

    /// <summary>
    /// A wait in whole minutes, rounded up and at least one. A server can name any wait: one past what a whole number
    /// holds stops there, where the cast alone gave a number of its own.
    /// </summary>
    public static int WholeMinutes(TimeSpan delay) => (int)Math.Clamp(Math.Ceiling(delay.TotalMinutes), 1, int.MaxValue);

    /// <summary>
    /// The failures an API call reports to the user instead of crashing: an error answer, a network
    /// failure, an unreadable answer, or a timeout (a cancellation the caller did not ask for).
    /// </summary>
    public static bool IsRequestFailure(Exception ex, CancellationToken cancellationToken = default) =>
        ex is SiloApiException or HttpRequestException or JsonException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    public static string ForTransport(Exception ex) =>
        ex is JsonException or HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse } ? Unexpected : Unreachable;

    /// <summary>Status and problem code, or the kind of failure; never a message that could echo input.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        SiloApiException api => $"{api.Status} {api.Code}",
        HttpRequestException http => $"network {http.HttpRequestError}",
        OperationCanceledException => "timeout",
        JsonException => "malformed response",
        _ => ex.GetType().Name,
    };
}
