using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>A week of airings.</summary>
public sealed partial class CalendarPage : Page
{
    private static readonly Brush Transparent = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush White = new SolidColorBrush(Colors.White);

    public CalendarPage()
    {
        ViewModel = App.Services.GetRequiredService<CalendarViewModel>();
        InitializeComponent();
        ViewModel.DayRequested += OnDayRequested;
    }

    public CalendarViewModel ViewModel { get; }

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

    // The web's day buttons: the picked day filled red, today tinted red, the others plain.
    public static Brush DayBackground(bool isSelected, bool isToday) =>
        isSelected ? Primary() : isToday ? new SolidColorBrush(PrimaryColor()) { Opacity = 0.15 } : Transparent;

    public static Brush DayLabelForeground(bool isSelected, bool isToday) =>
        isSelected ? new SolidColorBrush(Colors.White) { Opacity = 0.85 } : isToday ? Primary() : Muted();

    public static Brush DayNumberForeground(bool isSelected, bool isToday) => isSelected ? White : isToday ? Primary() : Text();

    public static Brush DayDotFill(bool isSelected, bool isToday) => isSelected ? White : isToday ? Primary() : Muted();

    public static double DayDotOpacity(bool hasEvents) => hasEvents ? 1 : 0;

    // Instance method because x:Bind function bindings call it on the page.
    public string LibraryName(SettingChoice library) => $"Library: {library.Label}";

    private static Brush Primary() => (Brush)Application.Current.Resources["SiloPrimaryBrush"];

    // The brush, not SiloPrimaryColor: the brush carries the server's accent (AccentPainter).
    private static Windows.UI.Color PrimaryColor() => ((SolidColorBrush)Application.Current.Resources["SiloPrimaryBrush"]).Color;

    private static Brush Muted() => (Brush)Application.Current.Resources["SiloMutedBrush"];

    private static Brush Text() => (Brush)Application.Current.Resources["SiloForegroundBrush"];

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

    // A day picked in the strip brings its row to the top of the days, an empty day's too; in a week without airings
    // the page says so at the top.
    private void OnDayRequested(object? sender, DateOnly day)
    {
        for (int index = 0; index < ViewModel.Rows.Count; index++)
        {
            if (ViewModel.Rows[index].Date == day)
            {
                var row = DayRows.GetOrCreateElement(index);
                // A row below the fold is only realized now: it needs its place before the scroll can reach it.
                row.UpdateLayout();
                row.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true });
                return;
            }
        }
        Scroller.ChangeView(null, 0, null);
    }
}
