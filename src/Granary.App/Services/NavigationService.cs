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
        [AppRoute.AddServer] = typeof(AddServerPage),
        [AppRoute.Player] = typeof(PlayerPage),
    };

    private Frame? _frame;

    public AppRoute? Current { get; private set; }

    public event EventHandler<AppRoute>? Navigated;

    public void Attach(Frame frame) => _frame = frame;

    public void NavigateTo(AppRoute route, object? parameter = null)
    {
        if (_frame is null || route == Current)
        {
            return;
        }
        _frame.Navigate(Pages[route], parameter);
        Current = route;
        Navigated?.Invoke(this, route);
    }
}
