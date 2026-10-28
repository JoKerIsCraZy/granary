using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Servers;

namespace Granary.Core.ViewModels;

// "Found nearby", as the Apple and Android apps list it on their server screen (DiscoveredServerList.swift,
// ServerSetupScreen.kt): the Silo servers advertised on this network and confirmed by their identity, at most four.
// The list shows nothing until something is found, so a network without servers looks as before. Picking one is the
// same as typing its address. The identity proves nothing (the id is public, the name the answerer's own), so a found
// server that matches a saved one, by id or by address, is not offered: that server is already in the list of saved
// servers, and something else answering for it on this network must not become a second, plain-HTTP entry. Every
// found address is plain HTTP; its row says so, and sign-in asks before a password goes there.
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
        if (IsSaved(row.Server))
        {
            // Saved since it was listed: the saved entry is the one to use.
            ShowFound();
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
        ?
        [
            .. _discovery.Servers
                .Where(server => !IsSaved(server))
                .Take(MaxFound)
                .Select((server, index) => new FoundServerRow(server, server.Url == _connectingTo, index == 0)),
        ]
        : [];

    private bool IsSaved(DiscoveredServer server) => _registry.Servers.Any(saved =>
        (saved.ServerId is { } id && string.Equals(id, server.ServerId, StringComparison.Ordinal))
        || Uri.Compare(saved.BaseUri, server.Url, UriComponents.HostAndPort, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0);
}

/// <summary>A found server's row: its mark, name and address, and whether it is being connected to.</summary>
/// <param name="IsFirst">The list's first row, which has no hairline above it.</param>
public sealed record FoundServerRow(DiscoveredServer Server, bool IsConnecting, bool IsFirst = false)
{
    public string Name => Server.Name;

    /// <summary>"On this network · 192.168.1.6:8090".</summary>
    public string Detail => Server.Detail;

    /// <summary>The mark's letter: the name's first.</summary>
    public string Initial => LanAdverts.Initial(Name);

    /// <summary>A found server answers plain HTTP: the row says "Not encrypted".</summary>
    public bool IsUnencrypted => Server.Url.Scheme == Uri.UriSchemeHttp;

    public string AccessibleName =>
        $"{Name}, {Detail}{(IsUnencrypted ? ", not encrypted" : "")}{(IsConnecting ? ", connecting" : "")}";
}
