using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Sends the user where the session state requires: add a server, sign in, change a temporary password,
/// pick a profile, or Home. Pages only change the session; they never navigate after sign-in themselves.
/// </summary>
public sealed partial class ShellViewModel(
    ISessionManager session,
    INavigationService navigation,
    IUiDispatcher dispatcher,
    ILogger<ShellViewModel> logger) : ObservableObject
{
    private (SessionState State, string? ServerId, string? ProfileId)? _routed;

    /// <summary>True once a profile acts on an active server; the app's navigation shows only then.</summary>
    [ObservableProperty]
    public partial bool IsSignedIn { get; private set; }

    /// <param name="initialRoute">Where to go instead of the state's page on start, for example the player.</param>
    public async Task StartAsync(AppRoute? initialRoute = null, object? parameter = null, CancellationToken cancellationToken = default)
    {
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
        Route();
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
