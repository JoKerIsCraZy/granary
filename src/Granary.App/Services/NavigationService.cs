using Granary.App.Views;
using Granary.Core.Abstractions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Services;

/// <summary>Maps <see cref="AppRoute"/> to pages in the main window's frame and keeps its back stack.</summary>
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
        [AppRoute.Search] = typeof(SearchPage),
        [AppRoute.Library] = typeof(LibraryPage),
        [AppRoute.Item] = typeof(ItemDetailPage),
        [AppRoute.Section] = typeof(SectionPage),
        [AppRoute.Settings] = typeof(SettingsPage),
    };

    private static readonly Dictionary<Type, AppRoute> Routes = Pages.ToDictionary(p => p.Value, p => p.Key);

    private Frame? _frame;

    public AppRoute? Current { get; private set; }

    /// <summary>The parameter the current page was opened with.</summary>
    public object? CurrentParameter { get; private set; }

    public bool CanGoBack => _frame?.CanGoBack == true;

    /// <summary>Raised after every navigation, Back included, and after the history was reset.</summary>
    public event EventHandler<AppRoute>? Navigated;

    public void Attach(Frame frame) => _frame = frame;

    public void NavigateTo(AppRoute route, object? parameter = null)
    {
        if (_frame is null || (route == Current && Equals(parameter, CurrentParameter)))
        {
            return;
        }
        _frame.Navigate(Pages[route], parameter);
        Arrived(route, parameter);
    }

    /// <summary>
    /// A sidebar section (Home, Search, a library), as the apps' tabs: it does not stack on the pages visited before.
    /// Only Home stays under it, so Back from a section goes Home and from Home nowhere; pages opened within a section
    /// stack on it as before. A section picked again goes back to its own page.
    /// </summary>
    public void ShowSection(AppRoute route, object? parameter = null)
    {
        if (_frame is null)
        {
            return;
        }
        if (route != Current || !Equals(parameter, CurrentParameter))
        {
            _frame.Navigate(Pages[route], parameter);
        }
        var home = _frame.BackStack.FirstOrDefault(e => e.SourcePageType == typeof(HomePage));
        _frame.BackStack.Clear();
        if (route != AppRoute.Home)
        {
            _frame.BackStack.Add(home ?? new PageStackEntry(typeof(HomePage), null, null));
        }
        Arrived(route, parameter);
    }

    public void ResetTo(AppRoute route, object? parameter = null)
    {
        if (_frame is null)
        {
            return;
        }
        _frame.Navigate(Pages[route], parameter);
        _frame.BackStack.Clear();
        Arrived(route, parameter);
    }

    public void GoBack()
    {
        if (_frame is not { CanGoBack: true } frame)
        {
            return;
        }
        var entry = frame.BackStack[^1];
        frame.GoBack();
        if (Routes.TryGetValue(entry.SourcePageType, out var route))
        {
            Arrived(route, entry.Parameter);
        }
    }

    private void Arrived(AppRoute route, object? parameter)
    {
        Current = route;
        CurrentParameter = parameter;
        Navigated?.Invoke(this, route);
    }
}
