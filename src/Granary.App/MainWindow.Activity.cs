using Granary.App.Views;
using Granary.Core.Abstractions;
using Granary.Core.Navigation;
using Granary.Core.ViewModels.Admin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Granary.App;

// The activity indicator of an acting administrator (Controls/ServerActivityButton), as the web places it: over the page
// at its top right, as the way back floats at its top left. On the admin area's pages always, elsewhere only while
// something is going on; never over the full player, which has the window to itself, but over the pages while the player
// is its bar.
public sealed partial class MainWindow
{
    private void InitializeActivity()
    {
        Activity.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerActivityViewModel.IsVisible))
            {
                ShowActivity();
            }
        };
        Activity.StreamsRequested += (_, _) => OpenAdminActivity();
    }

    /// <summary>What the server does, for the indicator.</summary>
    public ServerActivityViewModel Activity { get; }

    private bool ShowsActivity => ActivityButton.Visibility == Visibility.Visible;

    private void ShowActivity()
    {
        // The app is hidden under the full player (ShowApp), and the indicator with it.
        bool shown = Activity.IsVisible && Shell.IsSignedIn && Navigation.Visibility == Visibility.Visible;
        // Its panel belongs to the button: one that goes takes it along (the player opened over it).
        if (!shown)
        {
            ActivityButton.ClosePanel();
        }
        ActivityButton.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // "View all" beside Streams. Inside the admin area its pages switch in place, as its own sidebar switches them;
    // from anywhere else the area opens on Activity, and Back returns to where it was opened from.
    private void OpenAdminActivity()
    {
        if (_navigation.Current == AppRoute.Admin && ContentFrame.Content is AdminPage admin)
        {
            admin.Open(AdminPages.Activity);
        }
        else
        {
            _navigation.NavigateTo(AppRoute.Admin, AdminPages.Activity);
        }
    }

    // Up at the page's top edge goes to the indicator, as Left at its left edge goes to the sidebar: the button lies
    // outside the page, where the arrows do not reach by themselves. A held key stops at the edge.
    private bool EnterActivity(bool repeat)
    {
        if (!ShowsActivity || AreaOf(FocusedElement()) != FocusArea.Content || Root.XamlRoot is not { } root)
        {
            return false;
        }
        var next = FocusManager.FindNextElement(FocusNavigationDirection.Up, new FindNextElementOptions { SearchRoot = root.Content });
        if (next is not null && AreaOf(next) == FocusArea.Content)
        {
            return false;
        }
        return repeat || ActivityButton.FocusButton();
    }

    // Down or Left from the indicator goes back to where focus was on the page, else to its first element, as Right out
    // of the sidebar does.
    private bool LeaveActivity(bool repeat)
    {
        if (repeat || FocusedElement() is not { } focused || !IsWithin(focused, ActivityButton) || ContentFrame.Content is not Page page)
        {
            return false;
        }
        _pageFocus.TryGetValue(page, out var mark);
        return (RestoreFocus(page, mark) ?? FocusFirst(page)) is not null;
    }
}
