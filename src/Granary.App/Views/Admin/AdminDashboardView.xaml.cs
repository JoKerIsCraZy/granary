using Granary.App.Controls;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    public AdminDashboardView()
    {
        ViewModel = App.Services.GetRequiredService<AdminDashboardViewModel>();
        InitializeComponent();
        _clock.Tick += OnClockTick;
    }

    public AdminDashboardViewModel ViewModel { get; }

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
    private async void OnClockTick(object? sender, object e)
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

    public static string HealthGlyph(string label) => label switch
    {
        "Version" => "\uE8EC",
        "Uptime" => "\uE823",
        "Postgres" => "\uE8F1",
        "Redis" => "\uE945",
        "Nodes" => "\uE968",
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

    public static string EgressName(string peak) => $"Egress over the last 24 hours, in megabits per second. {peak}";

    public static GridLength Share(double fraction) => new(Math.Clamp(fraction, 0, 1), GridUnitType.Star);

    public static GridLength Rest(double fraction) => new(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star);

    public static string BarName(string rank, string label, string value, string secondary) => $"{rank}. {label}: {value}, {secondary}";

    public static string LibraryName(string name, string detail, string status) => $"{name}, {detail}, {status}";

    public static Brush LibraryDot(bool isEnabled) =>
        isEnabled ? new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)) : new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.3 };

    public static string UserName(string username, string role, string lastActive, string status) =>
        role.Length > 0 ? $"{username} ({role}), last active {lastActive}, {status}" : $"{username}, last active {lastActive}, {status}";

    // The web's outline badge for an active account, a red one for a disabled account.
    public static Brush StatusBorder(bool isEnabled) => isEnabled ? Resource("SiloBorderBrush") : new SolidColorBrush(Destructive);

    public static Brush StatusBackground(bool isEnabled) =>
        isEnabled ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : new SolidColorBrush(Destructive) { Opacity = 0.2 };

    /// <summary>" (Kids) started watching ", between the account and the title.</summary>
    public static string StartedWatching(string profileNote) => $"{profileNote} started watching ";

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
