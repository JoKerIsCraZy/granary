using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Discovery;
using Granary.Core.Servers;

namespace Granary.Core.ViewModels;

/// <summary>
/// "Add server" (docs/api-contract.md, 3.2): tries each candidate URL for the typed address, accepts the
/// first v2 server, stops at a server that needs updating or its first-time setup, saves the server with
/// its identity and display name, and goes to sign-in.
/// </summary>
public sealed partial class AddServerViewModel(
    IServerProbe probe,
    SystemApi system,
    IServerRegistry registry,
    INavigationService navigation) : ObservableObject
{
    internal const string UpdateRequiredMessage = "This Silo server is too old for this app. Update the server and try again.";
    internal const string NeedsSetupMessage = "This server still needs its first-time setup. Finish it in the server's web interface.";

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

        try
        {
            await TryCandidatesAsync(candidates, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectedServer = null;
            Status = "Cancelled.";
        }
    }

    private bool CanConnect() => !string.IsNullOrWhiteSpace(Address);

    private async Task TryCandidatesAsync(IReadOnlyList<Uri> candidates, CancellationToken cancellationToken)
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
            await AcceptAsync(candidate, setup, cancellationToken);
            return;
        }
        Status = $"No Silo server answered at that address ({lastReason}).";
    }

    private async Task AcceptAsync(Uri baseUri, SetupStatus? setup, CancellationToken cancellationToken)
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
        string name = branding?.ServerName is { } serverName && !string.IsNullOrWhiteSpace(serverName)
            ? serverName.Trim()
            : baseUri.Host;

        ServerEntry entry;
        try
        {
            entry = await registry.AddOrUpdateAsync(baseUri, name, serverId, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Granary could not save the server. Try again.";
            return;
        }
        Status = $"Found {entry.Name}.";
        navigation.NavigateTo(AppRoute.SignIn, entry.Id);
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
