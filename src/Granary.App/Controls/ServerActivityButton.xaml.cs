using System.ComponentModel;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Granary.App.Controls;

/// <summary>The activity indicator's button and panel, for the app's one <see cref="ServerActivityViewModel"/>.</summary>
public sealed partial class ServerActivityButton : UserControl
{
    // The web's pulse-opacity: down to half and back, once every two seconds.
    private const float BadgeDimmed = 0.5f;
    private static readonly TimeSpan BadgeBreath = TimeSpan.FromSeconds(2);

    // Windows' "Animation effects", read once when the app starts: turned off, the count stands still.
    private static readonly bool Breathes = new UISettings().AnimationsEnabled;

    private bool _breathing;

    public ServerActivityButton()
    {
        ViewModel = App.Services.GetRequiredService<ServerActivityViewModel>();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelChanged;
            ShowBreath();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ShowBreath();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => ShowBreath());
    }

    public ServerActivityViewModel ViewModel { get; }

    /// <summary>The panel is open.</summary>
    public bool IsPanelOpen => Panel.IsOpen;

    /// <summary>Moves the keyboard to the button, as the remote's Up at a page's top edge does.</summary>
    public bool FocusButton() => Trigger.Focus(FocusState.Keyboard);

    /// <summary>Closes the panel; false when it was not open.</summary>
    public bool ClosePanel()
    {
        if (!Panel.IsOpen)
        {
            return false;
        }
        Panel.Hide();
        return true;
    }

    // Instance method because x:Bind function bindings in this markup call it on the control. The web's text-primary while
    // something is going on, its muted foreground otherwise.
    public Brush PulseBrush(bool hasActivity) => (Brush)Application.Current.Resources[hasActivity ? "SiloPrimaryBrush" : "SiloMutedBrush"];

    private void OnPanelOpened(object? sender, object e) => ViewModel.IsPanelOpen = true;

    private void OnPanelClosed(object? sender, object e) => ViewModel.IsPanelOpen = false;

    // As the web's link: the panel closes, then the page opens.
    private void OnViewStreamsClick(object sender, RoutedEventArgs e)
    {
        Panel.Hide();
        ViewModel.ViewStreamsCommand.Execute(null);
    }

    // Up and Down walk the panel's rows and its link in the order they are read, and stop at its ends. The rows' scroller
    // would take the arrows to scroll by lines instead, past rows that never got focus: taken here on their way down, it
    // only follows the row that has focus.
    private void OnPanelPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down))
        {
            return;
        }
        e.Handled = true;
        var stops = new List<Control>();
        CollectStops(PanelRoot, stops);
        int at = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Control focused ? stops.IndexOf(focused) : -1;
        int next = at < 0 ? 0 : at + (e.Key == VirtualKey.Down ? 1 : -1);
        if (next >= 0 && next < stops.Count)
        {
            stops[next].Focus(FocusState.Keyboard);
        }
    }

    // What takes focus in the panel, in the order of the tree; nothing of a part that does not show.
    private static void CollectStops(DependencyObject element, List<Control> stops)
    {
        if (element is UIElement { Visibility: Visibility.Collapsed })
        {
            return;
        }
        if (element is Control { IsTabStop: true, IsEnabled: true } control)
        {
            stops.Add(control);
            return;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            CollectStops(VisualTreeHelper.GetChild(element, index), stops);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerActivityViewModel.HasActivity))
        {
            ShowBreath();
        }
    }

    // On the compositor, so it costs the UI thread nothing; only while the count shows.
    private void ShowBreath()
    {
        bool breathe = Breathes && IsLoaded && Visibility == Visibility.Visible && ViewModel.HasActivity;
        if (breathe == _breathing)
        {
            return;
        }
        _breathing = breathe;
        var visual = ElementCompositionPreview.GetElementVisual(Badge);
        if (!breathe)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 1;
            return;
        }
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.42f, 0), new System.Numerics.Vector2(0.58f, 1));
        var breath = compositor.CreateScalarKeyFrameAnimation();
        breath.InsertKeyFrame(0, 1);
        breath.InsertKeyFrame(0.5f, BadgeDimmed, ease);
        breath.InsertKeyFrame(1, 1, ease);
        breath.Duration = BadgeBreath;
        breath.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(nameof(Visual.Opacity), breath);
    }
}
