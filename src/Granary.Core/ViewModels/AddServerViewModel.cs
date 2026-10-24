using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Servers;

namespace Granary.Core.ViewModels;

/// <summary>
/// "Add server": tries each candidate URL for the typed address and stops at the first v2 server or
/// at a server that must be updated. Sign-in follows in Phase 3.
/// </summary>
public sealed partial class AddServerViewModel(IServerProbe probe) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial Uri? ConnectedServer { get; set; }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectedServer = null;
        var candidates = ServerAddress.Candidates(Address);
        if (candidates.Count == 0)
        {
            Status = "That is not a server address.";
            return;
        }

        string lastReason = "";
        foreach (var candidate in candidates)
        {
            Status = $"Trying {candidate}…";
            switch (await probe.ProbeAsync(candidate, cancellationToken))
            {
                case ServerProbeResult.Compatible found:
                    ConnectedServer = found.BaseUri;
                    Status = $"Found a Silo server at {found.BaseUri} (API v{found.Info.ApiMajor}).";
                    return;
                case ServerProbeResult.UpdateRequired:
                    Status = "This Silo server is too old for this app. Update the server and try again.";
                    return;
                case ServerProbeResult.NoVerdict noVerdict:
                    lastReason = noVerdict.Reason;
                    break;
            }
        }
        Status = $"No Silo server answered at that address ({lastReason}).";
    }

    private bool CanConnect() => !string.IsNullOrWhiteSpace(Address);
}
