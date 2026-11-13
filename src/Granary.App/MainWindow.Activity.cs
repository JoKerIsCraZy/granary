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
        bool isAppShown = Shell.IsSignedIn && Navigation.Visibility == Visibility.Visible;
        bool shown = Activity.IsVisible && isAppShown;
        if (!shown)
        {
            // A button that goes while it holds focus (its last activity ended, or its panel closed on a page where it
            // shows only while open) hands focus back to the page: left to itself, focus went to whatever comes next in
            // the tab order. Asked before the panel closes, which moves focus to the button.
            bool givesFocusBack = ActivityIndicatorFocus.GivesFocusBack(ActivityButton.HoldsFocus, isAppShown);
            // Its panel belongs to the button: one that goes takes it along (the player opened over it).
            ActivityButton.ClosePanel();
            if (givesFocusBack)
            {
                FocusPageFromActivity(_lastInputWasKey ? FocusState.Keyboard : FocusState.Programmatic);
            }
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
    // outside the page, where the arrows do not reach by themselves. Not past the way back, which lies at the page's top
    // too (ActivityIndicatorFocus). A held key stops at the edge.
    private bool EnterActivity(bool repeat)
    {
        var focused = FocusedElement();
        var from = ReferenceEquals(focused, PageBackButton) ? ActivityFocusPlace.PageBack
            : AreaOf(focused) == FocusArea.Content ? ActivityFocusPlace.Page
            : ActivityFocusPlace.Elsewhere;
        if (!ShowsActivity || from == ActivityFocusPlace.Elsewhere || Root.XamlRoot is not { } root)
        {
            return false;
        }
        var next = FocusManager.FindNextElement(FocusNavigationDirection.Up, new FindNextElementOptions { SearchRoot = root.Content });
        return ActivityIndicatorFocus.Up(ShowsActivity, from, UpTargetOf(next), repeat) switch
        {
            ActivityStep.FocusIndicator => ActivityButton.FocusButton(),
            ActivityStep.Stay => true,
            _ => false,
        };
    }

    private UpTarget UpTargetOf(DependencyObject? next) =>
        next is null ? UpTarget.Nothing
        : ReferenceEquals(next, PageBackButton) ? UpTarget.PageBack
        : AreaOf(next) == FocusArea.Content ? UpTarget.Page
        : UpTarget.Elsewhere;

    // Down or Left from the indicator goes back to where focus was on the page, else to its first element, as Right out
    // of the sidebar does.
    private bool LeaveActivity(bool repeat) =>
        !repeat && FocusedElement() is { } focused && IsWithin(focused, ActivityButton) && FocusPageFromActivity(FocusState.Keyboard);

    private bool FocusPageFromActivity(FocusState state)
    {
        if (ContentFrame.Content is not Page page)
        {
            return false;
        }
        _pageFocus.TryGetValue(page, out var mark);
        return (RestoreFocus(page, mark, state) ?? FocusFirst(page, state)) is not null;
    }
}
