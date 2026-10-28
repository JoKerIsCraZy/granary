using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Servers;

namespace Granary.Core.ViewModels;

// "Found nearby", as the Apple and Android apps list it on their server screen (DiscoveredServerList.swift,
// ServerSetupScreen.kt): the Silo servers advertised on this network and confirmed by their identity, at most four.
// The list shows nothing until something is found, so a network without servers looks as before. Picking one is the
// same as typing its address.
public sealed partial class AddServerViewModel
{
    /// <summary>Rows the list shows at most, as the apps.</summary>
    internal const int MaxFound = 4;

    private bool _discovering;

    /// <summary>The servers found on this network.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFound))]
    public partial IReadOnlyList<FoundServerRow> Found { get; private set; } = [];

    public bool HasFound => Found.Count > 0;

    /// <summary>A connect is on its way, typed or picked: the other waits, and Cancel shows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(ConnectToFoundCommand))]
    public partial bool IsBusy { get; private set; }

    // The picked row shows that it connects.
    private Uri? _connectingTo;

    /// <summary>Lists what is found on this network while the screen shows.</summary>
    public void StartDiscovery()
    {
        if (_discovering)
        {
            return;
        }
        _discovering = true;
        _discovery.Changed += OnDiscoveryChanged;
        _discovery.Start();
        ShowFound();
    }

    public void StopDiscovery()
    {
        if (!_discovering)
        {
            return;
        }
        _discovering = false;
        _discovery.Changed -= OnDiscoveryChanged;
        _discovery.Stop();
        Found = [];
    }

    /// <summary>Connects to a found server through the typed address's checks; it must still answer as that server.</summary>
    [RelayCommand(CanExecute = nameof(CanConnectToFound), IncludeCancelCommand = true)]
    private async Task ConnectToFoundAsync(FoundServerRow? row, CancellationToken cancellationToken)
    {
        if (row is null)
        {
            return;
        }
        ConnectedServer = null;
        IsBusy = true;
        _connectingTo = row.Server.Url;
        ShowFound();
        try
        {
            await TryCandidatesAsync([row.Server.Url], row.Server.ServerId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConnectedServer = null;
            Status = "Cancelled.";
        }
        finally
        {
            _connectingTo = null;
            IsBusy = false;
            ShowFound();
        }
    }

    /// <summary>Cancel: whichever connect is on its way.</summary>
    [RelayCommand]
    private void CancelConnect()
    {
        ConnectCommand.Cancel();
        ConnectToFoundCommand.Cancel();
    }

    private bool CanConnectToFound(FoundServerRow? row) => row is not null && !IsBusy;

    private void OnDiscoveryChanged(object? sender, EventArgs e) => _dispatcher.Post(ShowFound);

    private void ShowFound() => Found = _discovering
        ? [.. _discovery.Servers.Take(MaxFound).Select((server, index) => new FoundServerRow(server, server.Url == _connectingTo, index == 0))]
        : [];
}

/// <summary>A found server's row: its mark, name and address, and whether it is being connected to.</summary>
/// <param name="IsFirst">The list's first row, which has no hairline above it.</param>
public sealed record FoundServerRow(DiscoveredServer Server, bool IsConnecting, bool IsFirst = false)
{
    public string Name => Server.Name;

    /// <summary>"On this network · 192.168.1.6:8090".</summary>
    public string Detail => Server.Detail;

    /// <summary>The mark's letter: the name's first.</summary>
    public string Initial => Name.Length > 0 && char.IsLetterOrDigit(Name[0]) ? char.ToUpperInvariant(Name[0]).ToString() : "S";

    public string AccessibleName => IsConnecting ? $"{Name}, {Detail}, connecting" : $"{Name}, {Detail}";
}
