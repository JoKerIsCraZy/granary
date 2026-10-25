using System.Globalization;
using Granary.Core.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Granary.App.Controls;

/// <summary>The Subtitle appearance panel over the player; it edits <see cref="PlayerViewModel.Look"/>, which saves itself.</summary>
public sealed partial class SubtitleAppearancePanel : UserControl
{
    // The body scrolls past 52 % of the window, as the web's does; the card stays within the window.
    private const double BodyShare = 0.52;
    private const double CardInset = 32;

    private PlayerViewModel? _viewModel;

    public SubtitleAppearancePanel()
    {
        InitializeComponent();
        Backdrop.SizeChanged += (_, e) =>
        {
            Card.MaxHeight = Math.Max(0, e.NewSize.Height - CardInset);
            Body.MaxHeight = e.NewSize.Height * BodyShare;
        };
    }

    // Null until the page sets it: x:Bind skips what it cannot reach.
    public PlayerViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    public void FocusFirst() => DispatcherQueue.TryEnqueue(() => CloseButton.Focus(FocusState.Programmatic));

    public static Brush Ring(bool isSelected) => new SolidColorBrush(isSelected ? Colors.White : Colors.Transparent);

    // The chosen colour sits inside its ring.
    public static double SwatchSize(bool isSelected) => isSelected ? 24 : 28;

    // Upper case on screen only; screen readers get the line as written.
    public string Upper(string text) => text.ToUpper(CultureInfo.CurrentCulture);

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => _viewModel?.CloseAppearanceCommand.Execute(null);

    private void OnCardTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            // A typed opacity counts: the box hands its text over when it loses the keyboard, as the web's blurs first.
            CloseButton.Focus(FocusState.Programmatic);
            _viewModel?.CloseAppearanceCommand.Execute(null);
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex } swatch || _viewModel is not { } viewModel)
        {
            return;
        }
        DependencyObject? parent = swatch;
        while (parent is not null and not ItemsControl)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        switch ((parent as ItemsControl)?.Tag as string)
        {
            case "font":
                viewModel.Look.PickFontColor(hex);
                break;
            case "background":
                viewModel.Look.PickBackgroundColor(hex);
                break;
        }
    }
}
