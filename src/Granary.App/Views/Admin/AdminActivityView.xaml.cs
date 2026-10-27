using System.ComponentModel;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views.Admin;

/// <summary>
/// Admin → Activity. Its clock ticks every second, so a stream's time runs; the view model reads the streams again every
/// ten seconds. A stream's commands are the web's: Terminate beside its menu (asking first, as it cannot be undone), and
/// Pause or Resume, Stop and Message in the menu.
/// </summary>
public sealed partial class AdminActivityView : UserControl
{
    private const int MessageMaxLength = 500;

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public AdminActivityView()
    {
        ViewModel = App.Services.GetRequiredService<AdminActivityViewModel>();
        Sidebar = App.Services.GetRequiredService<AdminSidebarViewModel>();
        InitializeComponent();
        _clock.Tick += OnClockTick;
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    public AdminActivityViewModel ViewModel { get; }

    /// <summary>The live count beside the title, shared with the admin sidebar.</summary>
    public AdminSidebarViewModel Sidebar { get; }

    /// <summary>Shows the streams and starts the clock.</summary>
    public async Task ActivateAsync()
    {
        _clock.Start();
        try
        {
            await ViewModel.ActivateAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The activity could not be shown");
        }
    }

    /// <summary>The page left or showed another of its pages.</summary>
    public void Deactivate()
    {
        _clock.Stop();
        ViewModel.Deactivate();
    }

    /// <summary>Looks up an address, as the web's link from a stream's address does.</summary>
    public Task LookUpAsync(string ip)
    {
        ViewModel.IpQuery = ip;
        return ViewModel.LookupCommand.ExecuteAsync(null);
    }

    public static InfoBarSeverity NoticeSeverity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public static Brush MessageBrush(bool failed) =>
        failed ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44)) : (Brush)Application.Current.Resources["SiloMutedBrush"];

    public static double DimmedOpacity(bool isDimmed) => isDimmed ? 0.3 : 1;

    public static string ShareName(string label, int count, bool isSelected) => $"{label} {count}{(isSelected ? ", filtering" : "")}";

    public static Visibility NotLoading(bool isLoading, bool hasError) => isLoading || hasError ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"MKV · HEVC · 2160p · TrueHD 7.1": what reaches the viewer, in one line.</summary>
    public static string StreamLine(string container, string video, string audio) => $"{container} · {video} · {audio}";

    /// <summary>"Started 5m ago · 5:00".</summary>
    public static string TimeLine(string started, string elapsed) => $"Started {started} · {elapsed}";

    public static string AddressLine(string ip, string location) => location.Length > 0 ? $"{ip} ({location})" : ip;

    public static double Percent(double progress) => progress * 100;

    public static string PauseGlyph(bool isPaused) => isPaused ? "\uE768" : "\uE769";

    /// <summary>The page leaves for good: its bindings let go of the shared admin sidebar.</summary>
    public void Release() => Bindings.StopTracking();

    // async void: nothing may escape a timer's handler.
    private async void OnClockTick(object? sender, object e)
    {
        try
        {
            await ViewModel.TickAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The activity could not be read again");
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdminActivityViewModel.Methods))
        {
            ShowMethodBar();
        }
    }

    // The share of each method as one bar, in the web's order and colours.
    private void ShowMethodBar()
    {
        MethodBar.Children.Clear();
        MethodBar.ColumnDefinitions.Clear();
        foreach (var share in ViewModel.Methods)
        {
            MethodBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share.Count, GridUnitType.Star) });
            var part = new Border { Background = AdminDashboardView.MethodBrush(share.Method) };
            ToolTipService.SetToolTip(part, $"{share.Label}: {share.Count}");
            Grid.SetColumn(part, MethodBar.ColumnDefinitions.Count - 1);
            MethodBar.Children.Add(part);
        }
    }

    private void OnNoticeClosed(InfoBar sender, object args) => ViewModel.DismissNoticeCommand.Execute(null);

    private void OnIpKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && ViewModel.LookupCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.LookupCommand.Execute(null);
        }
    }

    private void OnMethodClick(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleMethodCommand.Execute((sender as FrameworkElement)?.Tag as AdminMethodShare);

    private void OnNodeClick(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleNodeCommand.Execute((sender as FrameworkElement)?.Tag as AdminNodeShare);

    private void OnTitleClick(object sender, RoutedEventArgs e) => ViewModel.OpenTitleCommand.Execute((sender as FrameworkElement)?.Tag as string);

    private void OnPauseClick(object sender, RoutedEventArgs e) => ViewModel.TogglePauseCommand.Execute(RowOf(sender));

    private void OnStopClick(object sender, RoutedEventArgs e) => ViewModel.StopCommand.Execute(RowOf(sender));

    private async void OnLookupAddressClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string { Length: > 0 } ip)
        {
            await Guard(() => LookUpAsync(ip), "An address could not be looked up");
        }
    }

    // Terminate revokes the session's playback for good: it asks first.
    private async void OnTerminateClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Terminate playback?",
            Content = $"{row.Username} stops watching {row.Title}. The session's playback is revoked for good; the player is told to stop as soon as it can be reached.",
            PrimaryButtonText = "Terminate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        await Guard(async () =>
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.TerminateCommand.ExecuteAsync(row);
            }
        }, "A session could not be terminated");
    }

    // The web's Send Message dialog: a text the player shows.
    private async void OnMessageClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { CanControl: true } row)
        {
            return;
        }
        var text = new TextBox
        {
            PlaceholderText = "Server restart in 5 minutes. Please finish this episode soon.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = MessageMaxLength,
            MinHeight = 96,
        };
        AutomationProperties.SetName(text, "Message");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"Send a custom message to {row.Username} during playback.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(text);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Send Message",
            Content = content,
            PrimaryButtonText = "Send Message",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            // Nothing to send until there is text: the dialog stays open with what was typed.
            IsPrimaryButtonEnabled = false,
        };
        text.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = text.Text.Trim().Length > 0;
        await Guard(async () =>
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.SendMessageAsync(row, text.Text);
            }
        }, "A message could not be sent");
    }

    private static AdminSessionRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as AdminSessionRow;

    private static async Task Guard(Func<Task> action, string message)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log(ex, message);
        }
    }

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<AdminActivityView>>().LogError(ex, "{Message}", message);
}
