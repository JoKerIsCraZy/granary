using Granary.App.Controls;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Granary.App.Views.Admin;

/// <summary>
/// Admin → Dashboard. While it shows, its clock moves "Updated …" on every half minute, and the view model reads the
/// dashboard again once a minute, as the web polls it.
/// </summary>
public sealed partial class AdminDashboardView : UserControl
{
    private const int PosterDecodeWidth = 140;

    // The web's status colours: --success, --info, --warning and --destructive.
    private static readonly Color Success = Color.FromArgb(0xFF, 0x5B, 0xC8, 0x70);
    private static readonly Color Info = Color.FromArgb(0xFF, 0x58, 0xA0, 0xF0);
    private static readonly Color Warning = Color.FromArgb(0xFF, 0xE9, 0xB4, 0x4C);
    private static readonly Color Destructive = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);

    private readonly DispatcherQueueTimer _clock;

    public AdminDashboardView()
    {
        ViewModel = App.Services.GetRequiredService<AdminDashboardViewModel>();
        InitializeComponent();
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(30);
        WeakTick.Subscribe(_clock, this, static view => view.OnClockTick());
    }

    public AdminDashboardViewModel ViewModel { get; }

    // The cards' titles and messages over their windows, as the web's range titles and phrases put them together.
    public string PlaybackTitle => AdminText.LastDay(AppText.Get("web.admin.dashboard.widgets.playbackActivity.title"));

    public string PlaybackChartName => AppText.Get("web.admin.dashboard.widgets.playbackActivity.chartLabelHourly", ("range", AdminText.LastDayPhrase));

    public string PlaybackEmpty => AppText.Get("web.admin.dashboard.widgets.playbackActivity.empty", ("range", AdminText.LastDayPhrase));

    public string EgressTitle => AdminText.LastDay(AppText.Get("web.admin.dashboard.widgets.egress.title"));

    public string TopTitlesTitle => AdminText.LastWeek(AppText.Get("web.admin.dashboard.widgets.topTitles.title"));

    public string TopTitlesEmpty => AppText.Get("web.admin.dashboard.widgets.topTitles.empty", ("range", AdminText.LastWeekPhrase));

    public string TopProfilesTitle => AdminText.LastWeek(AppText.Get("web.admin.dashboard.widgets.topProfiles.title"));

    public string TopProfilesEmpty => AppText.Get("web.admin.dashboard.widgets.topProfiles.empty", ("range", AdminText.LastWeekPhrase));

    /// <summary>Shows the dashboard and starts its clock.</summary>
    public async Task ActivateAsync()
    {
        _clock.Start();
        try
        {
            await ViewModel.ActivateAsync();
        }
        catch (Exception ex)
        {
            // Failed reads are the cards' to show; this is anything else.
            Log(ex, "The dashboard could not be shown");
        }
    }

    /// <summary>The page leaves for good: its bindings let go of the view model.</summary>
    public void Release() => Bindings.StopTracking();

    /// <summary>The page left or showed another of its pages: the clock stops, a read still running is dropped.</summary>
    public void Deactivate()
    {
        _clock.Stop();
        ViewModel.Deactivate();
    }

    // async void: nothing may escape a timer's handler.
    private async void OnClockTick()
    {
        try
        {
            await ViewModel.TickAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The dashboard could not be read again");
        }
    }

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<AdminDashboardView>>().LogError(ex, "{Message}", message);

    public static Visibility Ready(LibraryLoadState state) => Show(state == LibraryLoadState.Ready);

    public static Visibility Empty(LibraryLoadState state) => Show(state == LibraryLoadState.Empty);

    public static Visibility Failed(LibraryLoadState state) => Show(state == LibraryLoadState.Error);

    public static Visibility Shown(string text) => Show(!string.IsNullOrEmpty(text));

    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterDecodeWidth);

    // A paused stream's poster dims, as on the web.
    public static double PosterOpacity(bool isPaused) => isPaused ? 0.45 : 1;

    public static string HealthGlyph(AdminHealthKind kind) => kind switch
    {
        AdminHealthKind.Version => "\uE8EC",
        AdminHealthKind.Uptime => "\uE823",
        AdminHealthKind.Postgres => "\uE8F1",
        AdminHealthKind.Redis => "\uE945",
        AdminHealthKind.Nodes => "\uE968",
        _ => "\uE7BA",
    };

    public static string HealthName(string label, string value, string detail) => detail.Length > 0 ? $"{label}: {value}, {detail}" : $"{label}: {value}";

    public static Brush ToneBrush(AdminTone tone) => tone switch
    {
        AdminTone.Warn => new SolidColorBrush(Warning),
        AdminTone.Error => new SolidColorBrush(Destructive),
        _ => Resource("SiloForegroundBrush"),
    };

    /// <summary>A method's tag: its colour as text, tinted behind it, as the web's activity badges.</summary>
    public static Brush MethodBrush(string method) => new SolidColorBrush(MethodColor(method));

    public static Brush MethodBackground(string method) => new SolidColorBrush(MethodColor(method)) { Opacity = 0.1 };

    public static Brush MethodBorder(string method) => new SolidColorBrush(MethodColor(method)) { Opacity = 0.15 };

    // The web's name for the chart, then its peak as a sentence of its own.
    public static string EgressName(string peak) =>
        $"{AppText.Get("web.admin.dashboard.widgets.egress.chartLabel", ("range", AdminText.LastDayPhrase))}. {peak}";

    public static GridLength Share(double fraction) => new(Math.Clamp(fraction, 0, 1), GridUnitType.Star);

    public static GridLength Rest(double fraction) => new(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star);

    public static string BarName(string rank, string label, string value, string secondary) => $"{rank}. {label}: {value}, {secondary}";

    public static string LibraryName(string name, string detail, string status) => $"{name}, {detail}, {status}";

    public static Brush LibraryDot(bool isEnabled) =>
        isEnabled ? new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)) : new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.3 };

    // The web's outline badge for an active account, a red one for a disabled account.
    public static Brush StatusBorder(bool isEnabled) => isEnabled ? Resource("SiloBorderBrush") : new SolidColorBrush(Destructive);

    public static Brush StatusBackground(bool isEnabled) =>
        isEnabled ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : new SolidColorBrush(Destructive) { Opacity = 0.2 };

    // "elena (Kids) started watching Dark" in the order the language has it: the account and the title in bold, the profile
    // (the web's pill) in brackets.
    private void OnRecentContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not TextBlock text || args.NewValue is not AdminActivityRow row)
        {
            return;
        }
        text.Inlines.Clear();
        foreach (var part in row.Runs)
        {
            var run = new Run { Text = part.Tags.Contains("pill") ? $"({part.Text})" : part.Text };
            if (part.Tags.Contains("user") || part.Tags.Contains("link"))
            {
                run.FontFamily = (FontFamily)Application.Current.Resources["SiloFontSemiBold"];
                run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                run.Foreground = Resource("SiloForegroundBrush");
            }
            text.Inlines.Add(run);
        }
    }

    private static Color MethodColor(string method) => method switch
    {
        AdminPlayMethod.Direct => Success,
        AdminPlayMethod.Remux => Info,
        AdminPlayMethod.DirectStream => Warning,
        AdminPlayMethod.Transcode => Destructive,
        _ => ((SolidColorBrush)Resource("SiloMutedBrush")).Color,
    };

    private static Visibility Show(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
