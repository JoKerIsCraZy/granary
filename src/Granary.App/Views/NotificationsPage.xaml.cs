using Granary.App.Controls;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>The profile's notifications.</summary>
public sealed partial class NotificationsPage : Page
{
    private const int PosterWidth = 44;
    private const double ColumnWidth = 768;

    // The Series view asks for its next page this far above its foot.
    private const double LoadMoreDistance = 600;

    private static readonly string BellGlyph = char.ConvertFromUtf32(0xEA8F);
    private static readonly string BellOffGlyph = char.ConvertFromUtf32(0xE7ED);

    private static readonly Brush ReadBackground = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush UnreadBackground = new SolidColorBrush(Color(0x0F));
    private static readonly Brush PillOnBackground = new SolidColorBrush(Color(0x1F));
    private static readonly Brush FailedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));

    public NotificationsPage()
    {
        ViewModel = App.Services.GetRequiredService<NotificationsViewModel>();
        InitializeComponent();
        BuildMenu(ReasonMenu, NotifiedSeriesViewModel.ReasonChoices, "SeriesReason", choice => ViewModel.Series.Reason = choice);
        BuildMenu(SeriesSortMenu, NotifiedSeriesViewModel.SortChoices, "SeriesSort", choice => ViewModel.Series.Sort = choice);
    }

    public NotificationsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            // Back keeps the inbox as it was; the sidebar reads it again.
            await ViewModel.ActivateAsync(isReturn: e.NavigationMode == NavigationMode.Back);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            App.Services.GetRequiredService<ILogger<NotificationsPage>>().LogError(ex, "The notifications could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Deactivate();
    }

    // Static because x:Bind function bindings in the row template call them on the type.
    public static Brush RowBackground(bool isUnread) => isUnread ? UnreadBackground : ReadBackground;

    public static FontFamily TitleFont(bool isUnread) =>
        (FontFamily)Application.Current.Resources[isUnread ? "SiloFontSemiBold" : "SiloFontMedium"];

    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterWidth);

    public static double SwitchOpacity(bool enabled) => enabled ? 1 : 0.5;

    // A series that won't notify is dimmed, as the web's row.
    public static double SeriesOpacity(bool notifies) => notifies ? 1 : 0.5;

    public static Brush SeriesTitleBrush(bool notifies) =>
        (Brush)Application.Current.Resources[notifies ? "SiloForegroundBrush" : "SiloMutedBrush"];

    // A reason that does not notify now: an outline, struck through.
    public static Brush PillBackground(bool isOff) => isOff ? ReadBackground : PillOnBackground;

    public static Thickness PillBorder(bool isOff) => new(isOff ? 1 : 0);

    public static Windows.UI.Text.TextDecorations PillDecorations(bool isOff) =>
        isOff ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;

    // Mute shows the silenced bell, Unmute the bell.
    public static string MuteGlyph(bool muted) => muted ? BellGlyph : BellOffGlyph;

    public Brush NoticeBrush(bool failed) => failed ? FailedBrush : (Brush)Application.Current.Resources["SiloMutedBrush"];

    public double Dimmed(bool refreshing) => refreshing ? 0.6 : 1;

    private static Windows.UI.Color Color(byte alpha) => Windows.UI.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);

    // A column capped by MaxWidth in a ScrollViewer ends up off centre, so its width is set here.
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => Column.Width = Math.Min(ColumnWidth, e.NewSize.Width);

    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => LoadMoreSeriesNearTheFoot();

    // A page that came in grew the column; one still short of the screen asks for the next.
    private void OnColumnSizeChanged(object sender, SizeChangedEventArgs e) => LoadMoreSeriesNearTheFoot();

    // The web's LoadMoreOnScroll: the Series view's foot coming into view loads the next page.
    private void LoadMoreSeriesNearTheFoot()
    {
        if (ViewModel.IsSeriesView && Scroller.ScrollableHeight - Scroller.VerticalOffset < LoadMoreDistance
            && ViewModel.Series.LoadMoreOnScrollCommand.CanExecute(null))
        {
            ViewModel.Series.LoadMoreOnScrollCommand.Execute(null);
        }
    }

    // "See which series notify you" under the switches.
    private void OnSeeSeriesClick(object sender, RoutedEventArgs e)
    {
        PreferencesFlyout.Hide();
        ViewModel.ShowSeriesCommand.Execute(null);
    }

    // "Preferences" in the Series view's words opens the popover beside the title.
    private void OnSeriesPreferencesClick(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args) =>
        PreferencesFlyout.ShowAt(PreferencesButton);

    private void OnReasonMenuOpening(object? sender, object e) => CheckChosen(ReasonMenu, ViewModel.Series.Reason);

    private void OnSeriesSortMenuOpening(object? sender, object e) => CheckChosen(SeriesSortMenu, ViewModel.Series.Sort);

    private static void BuildMenu(MenuFlyout menu, IReadOnlyList<SettingChoice> choices, string group, Action<SettingChoice> choose)
    {
        foreach (var choice in choices)
        {
            var item = new RadioMenuFlyoutItem { Text = choice.Label, GroupName = group, Tag = choice };
            item.Click += (_, _) => choose(choice);
            menu.Items.Add(item);
        }
    }

    private static void CheckChosen(MenuFlyout menu, SettingChoice chosen)
    {
        foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = Equals(item.Tag, chosen);
        }
    }

    // The switches are read each time the popover opens, as the web's.
    private async void OnPreferencesOpening(object? sender, object e)
    {
        try
        {
            await ViewModel.Preferences.LoadAsync();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<NotificationsPage>>().LogError(ex, "The notification preferences could not be shown");
        }
    }
}
