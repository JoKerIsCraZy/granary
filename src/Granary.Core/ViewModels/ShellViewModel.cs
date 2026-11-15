using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Sends the user where the session state requires: add a server, sign in, change a temporary password,
/// pick a profile, or Home. Pages only change the session; they never navigate after sign-in themselves. A start that
/// cannot reach the server opens the remembered profile's downloads instead of Home (<see cref="OfflineStart"/>).
/// </summary>
public sealed partial class ShellViewModel(
    ISessionManager session,
    INavigationService navigation,
    IUiDispatcher dispatcher,
    ILogger<ShellViewModel> logger,
    OfflineStart? offline = null) : ObservableObject
{
    private (SessionState State, string? ServerId, string? ProfileId)? _routed;

    // On the UI thread. The session says where it stands while it is restored, before the start has asked whether the
    // server is there: that waits, or Home would open under the start screen ahead of the downloads.
    private bool _holdsRoute;

    /// <summary>True once a profile acts on an active server; the app's navigation shows only then.</summary>
    [ObservableProperty]
    public partial bool IsSignedIn { get; private set; }

    /// <param name="initialRoute">Where to go instead of the state's page on start, for example the player.</param>
    public async Task StartAsync(AppRoute? initialRoute = null, object? parameter = null, CancellationToken cancellationToken = default)
    {
        _holdsRoute = offline is not null && initialRoute is null;
        session.Changed += OnSessionChanged;
        try
        {
            await session.InitializeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Start in the state the session reached; at worst the user adds or signs in to a server again.
            logger.LogError(ex, "Could not restore the session");
        }
        if (initialRoute is { } route)
        {
            _routed = Key();
            IsSignedIn = session.State == SessionState.Ready;
            navigation.NavigateTo(route, parameter);
            return;
        }
        if (offline is not null && await StartsOfflineAsync(offline, cancellationToken))
        {
            return;
        }
        Route();
    }

    // Without the server, the remembered profile's downloads open, as Silo's Android app opens its downloads. The session
    // is ready all the same, so the navigation shows, and an account update that follows moves nobody.
    private async Task<bool> StartsOfflineAsync(OfflineStart start, CancellationToken cancellationToken)
    {
        bool isOffline;
        try
        {
            isOffline = await start.CheckAtStartAsync(cancellationToken);
        }
        finally
        {
            // Whatever became of the question, the session's changes route again.
            _holdsRoute = false;
        }
        var key = Key();
        if (!isOffline || key.State != SessionState.Ready)
        {
            return false;
        }
        _routed = key;
        IsSignedIn = true;
        logger.LogDebug("Routing to the downloads: the server cannot be reached");
        navigation.ResetTo(AppRoute.Downloads);
        return true;
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (dispatcher.HasThreadAccess)
        {
            Route();
        }
        else
        {
            dispatcher.Post(Route);
        }
    }

    private void Route()
    {
        if (_holdsRoute)
        {
            return;
        }
        IsSignedIn = session.State == SessionState.Ready;
        // Account updates also raise Changed; only a new state, server or profile moves the user.
        var key = Key();
        if (_routed == key)
        {
            return;
        }
        _routed = key;
        logger.LogDebug("Routing for session state {State}", key.State);
        // A new state starts a new history: Back never returns to sign-in, nor to another profile's pages.
        switch (key.State)
        {
            case SessionState.NoServer:
                navigation.ResetTo(AppRoute.AddServer);
                break;
            case SessionState.SignedOut:
                navigation.ResetTo(AppRoute.SignIn, key.ServerId);
                break;
            case SessionState.PasswordChangeRequired:
                navigation.ResetTo(AppRoute.ChangePassword);
                break;
            case SessionState.ProfileRequired:
                navigation.ResetTo(AppRoute.ProfilePicker);
                break;
            case SessionState.Ready:
                navigation.ResetTo(AppRoute.Home);
                break;
        }
    }

    private (SessionState State, string? ServerId, string? ProfileId) Key() =>
        (session.State, session.ActiveServer?.Id, session.ProfileId);
}
