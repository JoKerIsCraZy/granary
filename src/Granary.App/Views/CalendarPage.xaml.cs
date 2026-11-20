using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace Granary.App.Views;

/// <summary>A week of airings as a grid of days.</summary>
public sealed partial class CalendarPage : Page
{
    public static readonly DependencyProperty IsWideProperty =
        DependencyProperty.Register(nameof(IsWide), typeof(bool), typeof(CalendarPage), new PropertyMetadata(true));

    // Below this width the seven columns would squeeze their cards under about 130 px: the days stack instead.
    private const double GridMinWidth = 920;

    // The mockup's: past days' cards at 62 %.
    private const double PastCardsOpacity = 0.62;

    private static readonly Brush Plain = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x13, 0x13, 0x16));
    private static readonly Brush Transparent = new SolidColorBrush(Colors.Transparent);

    public CalendarPage()
    {
        ViewModel = App.Services.GetRequiredService<CalendarViewModel>();
        InitializeComponent();
    }

    public CalendarViewModel ViewModel { get; }

    /// <summary>Whether the days fit side by side; the page's own, from its width.</summary>
    public bool IsWide
    {
        get => (bool)GetValue(IsWideProperty);
        set => SetValue(IsWideProperty, value);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            // Back keeps the week as it was; the sidebar reads it again.
            await ViewModel.ActivateAsync(isReturn: e.NavigationMode == NavigationMode.Back);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            App.Services.GetRequiredService<ILogger<CalendarPage>>().LogError(ex, "The calendar could not be shown");
        }
    }

    // Only one of the two is made: a collapsed repeater realizes nothing.
    public Visibility GridVisibility(bool isReady, bool isWide) => isReady && isWide ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListVisibility(bool isReady, bool isWide) => isReady && !isWide ? Visibility.Visible : Visibility.Collapsed;

    // Today's column: a faint tint of the server's accent, darker at the top, in a 1 px ring of it (the mockup's #ef444414 to
    // #ef444408 and #ef44444d, with the accent for the red). The brush, not SiloPrimaryColor, carries the accent (AccentPainter).
    public static Brush ColumnBackground(bool isToday)
    {
        if (!isToday)
        {
            return Plain;
        }
        var accent = PrimaryColor();
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(accent, 0x14), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(accent, 0x08), Offset = 1 });
        return brush;
    }

    public static Brush ColumnRing(bool isToday) => isToday ? new SolidColorBrush(WithAlpha(PrimaryColor(), 0x4D)) : Transparent;

    public static double CardsOpacity(bool isPast) => isPast ? PastCardsOpacity : 1;

    // Instance method because x:Bind function bindings call it on the page.
    public string LibraryName(SettingChoice library) => AppText.Get("granary.calendar.libraryName", ("library", library.Label));

    private static Windows.UI.Color PrimaryColor() => ((SolidColorBrush)Application.Current.Resources["SiloPrimaryBrush"]).Color;

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    private void OnDaysSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= GridMinWidth;
        if (wide != IsWide)
        {
            IsWide = wide;
        }
    }

    private void OnLibraryMenuOpening(object sender, object e)
    {
        LibraryMenu.Items.Clear();
        foreach (var library in ViewModel.LibraryChoices)
        {
            var item = new RadioMenuFlyoutItem { Text = library.Label, GroupName = "CalendarLibrary", IsChecked = library == ViewModel.SelectedLibrary };
            item.Click += (_, _) => ViewModel.SelectedLibrary = library;
            LibraryMenu.Items.Add(item);
        }
    }
}
