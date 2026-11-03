using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Views;

/// <summary>
/// A page that names where keyboard focus starts when it is opened with a remote or the keyboard, as Silo's TV app's
/// detail page starts on Play. Other pages start on their first element that takes focus (MainWindow.Remote.cs).
/// </summary>
internal interface IArrivalFocus
{
    /// <summary>The element to focus; null while the page has none yet (it is still loading).</summary>
    Control? ArrivalFocus { get; }
}
