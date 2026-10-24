using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Auth;
using Granary.Core.Servers;

namespace Granary.Core.ViewModels;

/// <summary>
/// The saved servers (docs/api-contract.md, 6.5): switch, sign out, remove, add. The shell navigates
/// when the session changes, so the commands here only ask the session manager and the registry.
/// </summary>
public sealed partial class ServersViewModel : ObservableObject, IDisposable
{
    private readonly IServerRegistry _registry;
    private readonly ISessionManager _session;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    public ServersViewModel(IServerRegistry registry, ISessionManager session, INavigationService navigation, IUiDispatcher dispatcher)
    {
        _registry = registry;
        _session = session;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _registry.Changed += OnChanged;
        _session.Changed += OnChanged;
        Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoServers))]
    public partial IReadOnlyList<ServerItemViewModel> Servers { get; private set; } = [];

    public bool HasNoServers => Servers.Count == 0;

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [RelayCommand]
    private Task SwitchAsync(ServerItemViewModel? server) => RunAsync(server, async s =>
    {
        await _session.ActivateServerAsync(s.Id);
        return "";
    });

    [RelayCommand]
    private Task SignOutAsync(ServerItemViewModel? server) => RunAsync(server, async s =>
    {
        await _session.SignOutAsync(s.Id);
        return $"Signed out of {s.Name}.";
    });

    /// <summary>
    /// Signs out, then forgets the server. A server that cannot be reached is still removed, otherwise a
    /// server that is gone for good could never be removed. When the active server is removed, the
    /// registry picks the most recently used remaining one, and the session follows it.
    /// </summary>
    [RelayCommand]
    private Task RemoveAsync(ServerItemViewModel? server) => RunAsync(server, async s =>
    {
        bool signedOut = true;
        try
        {
            await _session.SignOutAsync(s.Id);
        }
        catch (Exception ex) when (IsServerFailure(ex))
        {
            signedOut = false;
        }
        bool wasActive = _registry.ActiveServerId == s.Id;
        await _registry.RemoveAsync(s.Id);
        if (wasActive && _registry.ActiveServerId is { } next)
        {
            await _session.ActivateServerAsync(next);
        }
        return signedOut
            ? $"Removed {s.Name}."
            : $"Removed {s.Name}. The server did not confirm the sign-out.";
    });

    [RelayCommand]
    private void AddServer() => _navigation.NavigateTo(AppRoute.AddServer);

    public void Dispose()
    {
        _disposed = true;
        _registry.Changed -= OnChanged;
        _session.Changed -= OnChanged;
    }

    /// <summary>A failure the user can understand; never the exception text, which is not meant for people.</summary>
    internal static string Describe(Exception error, string serverName) => error switch
    {
        JsonException or HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse } =>
            $"{serverName} sent an answer Granary does not understand. Check the server address.",
        HttpRequestException => $"Could not reach {serverName}. Check the connection and try again.",
        TaskCanceledException => $"{serverName} did not answer in time. Try again.",
        SiloApiException { Status: 429 } => $"{serverName} is busy. Wait a moment and try again.",
        SiloApiException { Status: >= 500 } => $"{serverName} has a problem right now. Try again later.",
        SiloApiException api => $"{serverName} refused the request ({api.Code}).",
        CryptographicException => "Granary could not read or save its sign-in data on this computer.",
        _ => "Granary could not save its settings on this computer. Try again.",
    };

    // JsonException: a proxy or another app answered 200 with something that is not Silo JSON.
    private static bool IsServerFailure(Exception error) =>
        error is SiloApiException or HttpRequestException or JsonException or TaskCanceledException { InnerException: TimeoutException };

    private static bool IsLocalFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or CryptographicException;

    private async Task RunAsync(ServerItemViewModel? server, Func<ServerItemViewModel, Task<string>> action)
    {
        if (server is null || IsBusy)
        {
            return;
        }
        IsBusy = true;
        Status = "";
        try
        {
            Status = await action(server);
        }
        catch (Exception ex) when (IsServerFailure(ex) || IsLocalFailure(ex))
        {
            Status = Describe(ex, server.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.Post(Refresh);

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        string? activeId = _registry.ActiveServerId;
        string? accountName = _session.ActiveServer?.Id == activeId
            && _session.State is not (SessionState.NoServer or SessionState.SignedOut)
                ? _session.Account?.Username
                : null;
        Servers =
        [
            .. _registry.Servers.Select(s => new ServerItemViewModel(
                s.Id,
                s.Name,
                s.BaseUri.OriginalString,
                s.Id == activeId,
                s.Id == activeId ? accountName : null)),
        ];
    }
}
