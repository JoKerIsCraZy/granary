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

    private void OnPressForDiagnosis(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            ViewModel.CheckFollowsEngine();
            var point = e.GetCurrentPoint(null).Position;
            var hits = VisualTreeHelper.FindElementsInHostCoordinates(point, this).Take(PressHitsShown).Select(Describe);
            _autoHideLog.LogDebug(
                "A press on the player landed on {Target}; at that point, from the top: {Hits}; the bottom bar {Shown}, opacity "
                + "{Opacity:0.##}, takes presses {HitTest}; phase {Phase}, can seek {CanSeek}, Playing Next shown {NextUp}, docked "
                + "{Docked}, mini view {MiniView}",
                Describe(e.OriginalSource as DependencyObject), string.Join(" > ", hits), BottomChrome.Visibility, BottomChrome.Opacity,
                BottomChrome.IsHitTestVisible, ViewModel.Phase, ViewModel.CanSeek, ViewModel.IsNextUpScreenShown, IsDocked, IsMiniView);
        }
        catch (Exception ex)
        {
            // A diagnosis never gets in the way of the press.
            _autoHideLog.LogDebug("A press on the player could not be described ({Failure})", ex.GetType().Name);
        }
    }

    // "Button ExpandButton", "Grid": the kind, and the name where it has one.
    private static string Describe(DependencyObject? element) => element switch
    {
        null => "nothing",
        FrameworkElement { Name.Length: > 0 } named => $"{named.GetType().Name} {named.Name}",
        _ => element.GetType().Name,
    };
}
