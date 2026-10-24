using Granary.App.Views;
using Granary.Core.Abstractions;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Services;

/// <summary>Maps <see cref="AppRoute"/> to pages in the main window's frame.</summary>
public sealed class NavigationService : INavigationService
{
    private static readonly Dictionary<AppRoute, Type> Pages = new()
    {
        [AppRoute.Home] = typeof(HomePage),
        [AppRoute.Servers] = typeof(ServersPage),
        [AppRoute.AddServer] = typeof(AddServerPage),
        [AppRoute.SignIn] = typeof(SignInPage),
        [AppRoute.ProfilePicker] = typeof(ProfilePickerPage),
        [AppRoute.ChangePassword] = typeof(ChangePasswordPage),
        [AppRoute.Player] = typeof(PlayerPage),
    };

    private Frame? _frame;

    public AppRoute? Current { get; private set; }

    public event EventHandler<AppRoute>? Navigated;

    public void Attach(Frame frame) => _frame = frame;

    public void NavigateTo(AppRoute route, object? parameter = null)
    {
        if (_frame is null || (route == Current && parameter is null))
        {
            return;
        }
        _frame.Navigate(Pages[route], parameter);
        Current = route;
        Navigated?.Invoke(this, route);
    }
}
