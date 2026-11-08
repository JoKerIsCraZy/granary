using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>A TMDB title's watchlist icon: a bookmark, checked (<see cref="IsChecked"/>) while the title is on the watchlist.</summary>
public sealed partial class BookmarkIcon : UserControl
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(BookmarkIcon), new PropertyMetadata(false, (sender, _) => ((BookmarkIcon)sender).Show()));

    private static readonly Brush NoFill = new SolidColorBrush(Colors.Transparent);

    public BookmarkIcon()
    {
        InitializeComponent();
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Show());
        Show();
    }

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    private void Show()
    {
        Ribbon.Stroke = Foreground;
        Check.Stroke = Foreground;
        // The web's fill-primary/20 inside a checked bookmark, in the accent as it is now (AccentPainter repaints the tint).
        Ribbon.Fill = IsChecked ? (Brush)Application.Current.Resources["SiloPrimaryTint20Brush"] : NoFill;
        Check.Visibility = IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }
}
