using Granary.App.Views;
using Granary.Core.Abstractions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Services;

/// <summary>
/// Maps <see cref="AppRoute"/> to pages in the main window's frame and keeps its back stack. The player is not a page: its
/// route goes to the <see cref="IPlayerOverlay"/> over the window, and while that fills the window, Back closes it and any
/// other page closes it first, as leaving the player always ended playback.
/// </summary>
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
        [AppRoute.Search] = typeof(SearchPage),
        [AppRoute.Library] = typeof(LibraryPage),
        [AppRoute.Item] = typeof(ItemDetailPage),
        [AppRoute.Section] = typeof(SectionPage),
        [AppRoute.Settings] = typeof(SettingsPage),
        [AppRoute.List] = typeof(PersonalListPage),
        [AppRoute.Collections] = typeof(CollectionsPage),
        // A collection's titles show as a list does.
        [AppRoute.Collection] = typeof(PersonalListPage),
        [AppRoute.ForYou] = typeof(ForYouPage),
        [AppRoute.RecommendationSection] = typeof(RecommendationSectionPage),
        [AppRoute.Calendar] = typeof(CalendarPage),
        [AppRoute.Notifications] = typeof(NotificationsPage),
        [AppRoute.Requests] = typeof(RequestsPage),
        [AppRoute.Title] = typeof(TitlePage),
        [AppRoute.RequestTitles] = typeof(RequestTitlesPage),
        [AppRoute.Admin] = typeof(AdminPage),
        [AppRoute.CollectionEditor] = typeof(CollectionEditorPage),
    };

    private static readonly Dictionary<Type, AppRoute> Routes = Pages.Where(p => p.Key != AppRoute.Collection).ToDictionary(p => p.Value, p => p.Key);

    private Frame? _frame;
    private IPlayerOverlay? _player;

    public AppRoute? Current { get; private set; }

    /// <summary>The parameter the current page was opened with.</summary>
    public object? CurrentParameter { get; private set; }

    public bool CanGoBack => _player?.IsPlayerFull == true || _frame?.CanGoBack == true;

    /// <summary>Raised after every navigation, Back included, and after the history was reset.</summary>
    public event EventHandler<AppRoute>? Navigated;

    /// <summary>
    /// Asked before each navigation with the route it goes to: a transition to play instead of the frame's own, or
    /// null for the frame's own. The window replaces it where the sidebar changes its width with the page.
    /// </summary>
    public Func<AppRoute, NavigationTransitionInfo?>? TransitionFor { get; set; }

    public void Attach(Frame frame) => _frame = frame;

    public void AttachPlayer(IPlayerOverlay player) => _player = player;

    public void NavigateTo(AppRoute route, object? parameter = null)
    {
        if (route == AppRoute.Player)
        {
            _player?.Play(parameter);
            return;
        }
        if (Holds(() => NavigateTo(route, parameter)))
        {
            return;
        }
        CloseFullPlayer();
        if (_frame is null || (route == Current && Equals(parameter, CurrentParameter)))
        {
            return;
        }
        Navigate(route, parameter);
        Arrived(route, parameter);
    }

    /// <summary>
    /// A sidebar section (Home, Search, a library), as the apps' tabs: it does not stack on the pages visited before.
    /// Only Home stays under it, so Back from a section goes Home and from Home nowhere; pages opened within a section
    /// stack on it as before. A section picked again goes back to its own page.
    /// </summary>
    public void ShowSection(AppRoute route, object? parameter = null)
    {
        if (Holds(() => ShowSection(route, parameter)))
        {
            return;
        }
        CloseFullPlayer();
        if (_frame is null)
        {
            return;
        }
        if (route != Current || !Equals(parameter, CurrentParameter))
        {
            Navigate(route, parameter);
        }
        var home = _frame.BackStack.FirstOrDefault(e => e.SourcePageType == typeof(HomePage));
        _frame.BackStack.Clear();
        if (route != AppRoute.Home)
        {
            _frame.BackStack.Add(home ?? new PageStackEntry(typeof(HomePage), null, null));
        }
        Arrived(route, parameter);
    }

    /// <summary>A new start (a sign-in, another profile): the player closes too, even as the bar.</summary>
    public void ResetTo(AppRoute route, object? parameter = null)
    {
        if (_player?.IsPlayerOpen == true)
        {
            _player.ClosePlayer();
        }
        if (_frame is null)
        {
            return;
        }
        Navigate(route, parameter);
        _frame.BackStack.Clear();
        Arrived(route, parameter);
    }

    public void GoBack()
    {
        if (_player?.IsPlayerFull == true)
        {
            LeavePlayer();
            return;
        }
        if (_frame is not { CanGoBack: true } frame || Holds(GoBack))
        {
            return;
        }
        var entry = frame.BackStack[^1];
        AppRoute? route = Routes.TryGetValue(entry.SourcePageType, out var type)
            ? type == AppRoute.List && entry.Parameter is CollectionRouteParameter ? AppRoute.Collection : type
            : null;
        if (route is { } target && TransitionFor?.Invoke(target) is { } transition)
        {
            frame.GoBack(transition);
        }
        else
        {
            frame.GoBack();
        }
        if (route is { } arrived)
        {
            Arrived(arrived, entry.Parameter);
        }
    }

    private void Navigate(AppRoute route, object? parameter)
    {
        if (TransitionFor?.Invoke(route) is { } transition)
        {
            _frame!.Navigate(Pages[route], parameter, transition);
        }
        else
        {
            _frame!.Navigate(Pages[route], parameter);
        }
    }

    public void LeavePlayer()
    {
        if (_player?.IsPlayerOpen == true)
        {
            _player.ClosePlayer();
        }
        // Played from the start (--play): nothing is under the player.
        if (Current is null)
        {
            ResetTo(AppRoute.Home);
        }
    }

    private void CloseFullPlayer()
    {
        if (_player?.IsPlayerFull == true)
        {
            _player.ClosePlayer();
        }
    }

    // A page with something to lose asks first (ILeaveGuard); the navigation goes on once it lets go.
    private bool Holds(Action go)
    {
        if (_frame?.Content is not ILeaveGuard { AsksBeforeLeaving: true } guard)
        {
            return false;
        }
        _ = AskAsync(guard, go);
        return true;
    }

    private async Task AskAsync(ILeaveGuard guard, Action go)
    {
        if (await guard.ConfirmLeaveAsync())
        {
            go();
        }
        else if (Current is { } current)
        {
            // The page stayed: the sidebar shows it again.
            Navigated?.Invoke(this, current);
        }
    }

    private void Arrived(AppRoute route, object? parameter)
    {
        Current = route;
        CurrentParameter = parameter;
        Navigated?.Invoke(this, route);
    }
}
