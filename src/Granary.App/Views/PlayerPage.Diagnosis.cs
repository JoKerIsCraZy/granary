using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views;

// A diagnosis rather than a feature: a tester's clicks on the player did nothing while Space still paused it. Each press on
// the page says in the log what it landed on, what lies at that point from the top, and how the bottom bar stands, so an
// element that takes the clicks, or a bar that is hidden or off, shows. Only kinds and names of elements, nothing of the title.
public sealed partial class PlayerPage
{
    private const int PressHitsShown = 4;

    private void InitializeDiagnosis() =>
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPressForDiagnosis), handledEventsToo: true);

    // A press WinUI sees. The point is in the window's coordinates, as FindElementsInHostCoordinates takes it.
    private void OnPressForDiagnosis(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var point = e.GetCurrentPoint(null).Position;
            DiagnosePress("on the player", e.OriginalSource as DependencyObject,
                VisualTreeHelper.FindElementsInHostCoordinates(point, this).Take(PressHitsShown).Select(Describe));
        }
        catch (Exception ex)
        {
            // A diagnosis never gets in the way of the press.
            _autoHideLog.LogDebug("A press on the player could not be described ({Failure})", ex.GetType().Name);
        }
    }

    // A press the cursor shield took while the cursor was hidden, which WinUI does not see: what lies under the pointer.
    private void DiagnoseShieldPress(DependencyObject? under)
    {
        try
        {
            DiagnosePress("through the hidden cursor's shield", under, []);
        }
        catch (Exception ex)
        {
            _autoHideLog.LogDebug("A press on the player could not be described ({Failure})", ex.GetType().Name);
        }
    }

    private void DiagnosePress(string how, DependencyObject? target, IEnumerable<string> hits)
    {
        ViewModel.CheckFollowsEngine();
        _autoHideLog.LogDebug(
            "A press {How} landed on {Target}; at that point, from the top: {Hits}; the bottom bar {Shown}, opacity {Opacity:0.##}, "
            + "takes presses {HitTest}; phase {Phase}, can seek {CanSeek}, Playing Next shown {NextUp}, docked {Docked}, mini view "
            + "{MiniView}",
            how, Describe(target), string.Join(" > ", hits) is { Length: > 0 } list ? list : "not read", BottomChrome.Visibility,
            BottomChrome.Opacity, BottomChrome.IsHitTestVisible, ViewModel.Phase, ViewModel.CanSeek, ViewModel.IsNextUpScreenShown,
            IsDocked, IsMiniView);
    }

    // "Button ExpandButton", "Grid": the kind, and the name where it has one.
    private static string Describe(DependencyObject? element) => element switch
    {
        null => "nothing",
        FrameworkElement { Name.Length: > 0 } named => $"{named.GetType().Name} {named.Name}",
        _ => element.GetType().Name,
    };
}
