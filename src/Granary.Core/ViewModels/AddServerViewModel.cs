using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Discovery;
using Granary.Core.Auth;
using Granary.Core.Servers;

namespace Granary.Core.ViewModels;

/// <summary>
/// "Add server" (docs/api-contract.md, 3.2): tries each candidate URL for the typed address, accepts the
/// first v2 server, stops at a server that needs updating or its first-time setup, saves the server with
/// its identity and display name, and goes to sign-in. A saved server that is still signed in is opened again instead
/// and asks who is watching, as the apps do. Servers found on this network can be picked instead of typed
/// (<see cref="Found"/>); they go the same way.
/// </summary>
public sealed partial class AddServerViewModel(
    IServerProbe probe,
    SystemApi system,
    IServerRegistry registry,
    ISessionManager session,
    INavigationService navigation,
    LanServerDiscovery discovery,
    IUiDispatcher dispatcher) : ObservableObject
{
    internal const string UpdateRequiredMessage = "This Silo server is too old for this app. Update the server and try again.";
    internal const string NeedsSetupMessage = "This server still needs its first-time setup. Finish it in the server's web interface.";
    internal const string AnswersAsAnotherMessage = "That server no longer answers as the one found. Try again.";

    private readonly LanServerDiscovery _discovery = discovery;
    private readonly IUiDispatcher _dispatcher = dispatcher;
    private readonly IServerRegistry _registry = registry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    /// <summary>The Silo server the last connect found, or null.</summary>
    [ObservableProperty]
    public partial Uri? ConnectedServer { get; set; }

    [RelayCommand(CanExecute = nameof(CanConnect), IncludeCancelCommand = true)]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectedServer = null;
        var candidates = ServerAddress.Candidates(Address);
        if (candidates.Count == 0)
        {
            Status = "That is not a server address.";
            return;
        }

        IsBusy = true;
        try
        {
            await TryCandidatesAsync(candidates, expectedServerId: null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectedServer = null;
            Status = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnect() => !string.IsNullOrWhiteSpace(Address) && !IsBusy;

    // A found server goes the typed address's way, and must still answer as the server that was found.
    private async Task TryCandidatesAsync(IReadOnlyList<Uri> candidates, string? expectedServerId, CancellationToken cancellationToken)
    {
        string lastReason = "";
        foreach (var candidate in candidates)
        {
            Status = $"Trying {candidate.OriginalString}…";
            var verdict = await probe.ProbeAsync(candidate, cancellationToken);
            if (verdict is ServerProbeResult.UpdateRequired)
            {
                Status = UpdateRequiredMessage;
                return;
            }

            // Without a verdict from system/info, a v2 answer from system/setup still proves the server
            // (as Android does); for a proven server a failed setup call is tolerated.
            var setup = await TryGetAsync(ct => system.GetSetupStatusAsync(candidate, ct), cancellationToken);
            if (verdict is ServerProbeResult.NoVerdict noVerdict && setup is null)
            {
                lastReason = noVerdict.Reason;
                continue;
            }
            await AcceptAsync(candidate, setup, expectedServerId, cancellationToken);
            return;
        }
        Status = $"No Silo server answered at that address ({lastReason}).";
    }

    private async Task AcceptAsync(Uri baseUri, SetupStatus? setup, string? expectedServerId, CancellationToken cancellationToken)
    {
        ConnectedServer = baseUri;
        if (setup is { NeedsSetup: true })
        {
            Status = NeedsSetupMessage;
            return;
        }

        Status = $"Connecting to {baseUri.OriginalString}…";
        var identity = await TryGetAsync(ct => system.GetIdentityAsync(baseUri, ct), cancellationToken);
        var branding = await TryGetAsync(ct => system.GetBrandingAsync(baseUri, ct), cancellationToken);
        string? serverId = identity?.ServerId is { } id && !string.IsNullOrWhiteSpace(id) ? id : null;
        if (expectedServerId is not null && serverId != expectedServerId)
        {
            ConnectedServer = null;
            Status = AnswersAsAnotherMessage;
            return;
        }
        string name = branding?.ServerName is { } serverName && !string.IsNullOrWhiteSpace(serverName)
            ? serverName.Trim()
            : baseUri.Host;

        ServerEntry entry;
        try
        {
            // Added before and still signed in: the shell goes to its profiles. This is settled before the entry takes
            // the answer's identity, so a session saved for another server at this address is never handed on.
            bool reopened = SavedAt(baseUri) is { } saved && await ReopenAsync(saved, serverId, cancellationToken);
            entry = await _registry.AddOrUpdateAsync(baseUri, name, serverId, reopened ? CancellationToken.None : cancellationToken);
            if (reopened)
            {
                Status = $"Opening {entry.Name}…";
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Granary could not save the server. Try again.";
            return;
        }
        Status = $"Found {entry.Name}.";
        navigation.NavigateTo(AppRoute.SignIn, entry.Id);
    }

    private ServerEntry? SavedAt(Uri baseUri)
    {
        var address = ServerAddress.Normalize(baseUri);
        return _registry.Servers.FirstOrDefault(s => ServerAddress.Normalize(s.BaseUri) == address);
    }

    private async Task<bool> ReopenAsync(ServerEntry saved, string? serverId, CancellationToken cancellationToken)
    {
        try
        {
            return await session.ReopenServerAsync(saved.Id, serverId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Removed meanwhile: it is added as a new one.
            return false;
        }
    }

    /// <summary>
    /// An optional public call; null when it fails (unreachable, error status, not the expected document)
    /// or takes longer than the probe timeout. Cancellation by the caller still propagates.
    /// </summary>
    private static async Task<T?> TryGetAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ServerProbe.DefaultTimeout);
        try
        {
            return await call(timeout.Token);
        }
        catch (Exception ex) when (ex is SiloApiException or HttpRequestException or JsonException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
