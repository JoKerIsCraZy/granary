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
    private string? _listServerId;
    private bool _disposed;

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

    public static bool IsCompletePin([NotNullWhen(true)] string? pin) => pin is { Length: PinLength } && pin.All(char.IsAsciiDigit);

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
            ClosePinPrompt();
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
        if (server is null)
        {
            IsLoading = false;
            ShowList([], null);
            SetStatus(AppText.Get("granary.profiles.noServer"), isLoadError: false);
            return;
        }

        IsLoading = true;
        SetStatus("", isLoadError: false);
        try
        {
            var collection = await _profiles.ListAsync(server.BaseUri);
            if (generation != _loadGeneration)
            {
                return;
            }
            ShowList([.. collection.Items.Select(p => ProfileItemViewModel.From(p, server.BaseUri))], server.Id);
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
                _logger.LogWarning("PIN check for profile {ProfileId} was valid but carried no profile token", profileId);
                return new PinOutcome(null, AppText.Get("granary.profiles.pin.unconfirmed"), false);
            }
            return new PinOutcome(verification.ProfileToken, null, false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("PIN check for profile {ProfileId} failed: {Failure}", profileId, ApiFailureText.Describe(ex));
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
            _logger.LogInformation("Another server became active while profile {ProfileId} was picked; the pick was dropped", profileId);
            // The session may have moved on before this list was shown: the new server's profiles come either way.
            if (!ListBelongsToActiveServer(out _))
            {
                LoadCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Selecting profile {ProfileId} failed: {Failure}", profileId, ApiFailureText.Describe(ex));
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

    private void ShowList(IReadOnlyList<ProfileItemViewModel> profiles, string? serverId)
    {
        Profiles = profiles;
        _listServerId = serverId;
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
    }

    // Another server became active (or none): the shown list, a pending load and an open prompt belong
    // to the old one.
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (_disposed || !_hasLoaded || _session.ActiveServer?.Id == _requestedServerId)
        {
            return;
        }
        _loadGeneration++;
        IsLoading = false;
        ClosePinPrompt();
        ShowList([], null);
        SetStatus("", isLoadError: false);
        _requestedServerId = _session.ActiveServer?.Id;
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
        int minutes = Math.Max(1, (int)Math.Ceiling(delay.TotalMinutes));
        return AppText.Get("web.profiles.pin.lockedMinutes", ("count", minutes), ("count_text", minutes));
    }

    public void Dispose()
    {
        _disposed = true;
        _session.Changed -= OnSessionChanged;
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
        int minutes = Math.Max(1, (int)Math.Ceiling(delay.TotalMinutes));
        return AppText.Get("granary.apiFailure.tooManyRequests", ("count", minutes));
    }

    /// <summary>
    /// "Try again in N minutes." from a <c>Retry-After</c> delay, rounded up to whole minutes, for the callers that still
    /// put it after a sentence of their own (detail, playback, the password change); in English until they have whole
    /// messages.
    /// </summary>
    public static string TryAgainIn(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } delay)
        {
            return "Try again in a few minutes.";
        }
        int minutes = Math.Max(1, (int)Math.Ceiling(delay.TotalMinutes));
        return minutes == 1 ? "Try again in 1 minute." : $"Try again in {minutes} minutes.";
    }

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
