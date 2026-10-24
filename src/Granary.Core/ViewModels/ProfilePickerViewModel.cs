using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Servers;
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

    internal const string WrongPin = "That PIN is not right.";

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

    public string PinPromptTitle => PinProfile is { } profile ? $"Enter the PIN for {profile.Name}" : "Enter the PIN";

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
        if (!ListBelongsToActiveServer(out _))
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
        await CommitAsync(profile.Id, null);
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
            PinError = $"Enter all {PinLength} digits of the PIN.";
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
            await CommitAsync(profile.Id, token);
        }
        else if (outcome.ProfileChanged)
        {
            // The profile lost its PIN or is gone: show the current list.
            ClosePinPrompt();
            await LoadProfilesAsync();
            if (!HasLoadError && Profiles.Count > 0)
            {
                Status = "That profile has changed. Choose it again.";
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
            SetStatus("No server is selected.", isLoadError: false);
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
                SetStatus("This account has no profiles yet.", isLoadError: false);
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
                return new PinOutcome(null, "The server did not confirm the PIN. Try again.", false);
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

    private async Task CommitAsync(string profileId, string? profileToken)
    {
        IsSelecting = true;
        try
        {
            await _session.SelectProfileAsync(profileId, profileToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Selecting profile {ProfileId} failed: {Failure}", profileId, ApiFailureText.Describe(ex));
            SetStatus("Could not open the profile. Try again.", isLoadError: false);
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
        SiloApiException { Status: 429 } api => "The server is receiving too many requests. " + ApiFailureText.TryAgainIn(api.RetryAfter),
        SiloApiException { Status: 503 } => ApiFailureText.Busy,
        SiloApiException api => $"The server could not list the profiles (error {api.Status}).",
        _ => ApiFailureText.ForTransport(ex),
    };

    private static string DescribePinFailure(Exception ex) => ex switch
    {
        SiloApiException { Status: 429 } api => "Too many attempts. " + ApiFailureText.TryAgainIn(api.RetryAfter),
        SiloApiException { Status: 503 } => ApiFailureText.Busy,
        SiloApiException api => $"The PIN could not be checked (error {api.Status}).",
        _ => ApiFailureText.ForTransport(ex),
    };

    public void Dispose()
    {
        _disposed = true;
        _session.Changed -= OnSessionChanged;
    }

    private readonly record struct PinOutcome(string? ProfileToken, string? Error, bool ProfileChanged);
}

/// <summary>User-facing wording and log-safe summaries for failed API calls in the sign-in view models.</summary>
internal static class ApiFailureText
{
    public const string Busy = "The server is busy, try again.";
    public const string Unreachable = "Could not reach the server. Check the connection and try again.";
    public const string Unexpected = "The server sent an answer this app does not understand.";

    /// <summary>"Try again in N minutes." from a <c>Retry-After</c> delay, rounded up to whole minutes.</summary>
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
