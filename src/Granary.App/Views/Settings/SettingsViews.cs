using System.ComponentModel;
using System.Diagnostics;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views.Settings;

/// <summary>Settings → Playback; it reads the profile's values when it is shown.</summary>
public sealed partial class PlaybackSettingsView : UserControl
{
    public PlaybackSettingsView()
    {
        ViewModel = ActivatorUtilities.CreateInstance<PlaybackSettingsViewModel>(App.Services);
        InitializeComponent();
        // Once: the page keeps the view, and coming back to it must not overwrite what is shown.
        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        await SettingsViewLoad.RunAsync(ViewModel.LoadAsync);
    }

    public PlaybackSettingsViewModel ViewModel { get; }
}

/// <summary>Settings → Language &amp; Subtitles; it reads the profile's values when it is shown.</summary>
public sealed partial class LanguageSettingsView : UserControl
{
    public LanguageSettingsView()
    {
        ViewModel = ActivatorUtilities.CreateInstance<LanguageSettingsViewModel>(App.Services);
        InitializeComponent();
        Loaded += OnFirstLoaded;
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        await SettingsViewLoad.RunAsync(ViewModel.LoadAsync);
    }

    public LanguageSettingsViewModel ViewModel { get; }

    // The palette is the one of the list the swatch is in (its Tag).
    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex } swatch)
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
                ViewModel.Look.PickFontColor(hex);
                break;
            case "outline":
                ViewModel.Look.PickOutlineColor(hex);
                break;
            case "background":
                ViewModel.Look.PickBackgroundColor(hex);
                break;
        }
    }
}

/// <summary>
/// Settings → Navigation &amp; Cards; it reads the profile's poster settings and badges when it is shown, and lets go of them
/// and of the sidebar as the page leaves.
/// </summary>
public sealed partial class InterfaceSettingsView : UserControl, IDisposable
{
    public InterfaceSettingsView()
    {
        Posters = ActivatorUtilities.CreateInstance<PosterSettingsViewModel>(App.Services);
        ViewModel = ActivatorUtilities.CreateInstance<BadgeSettingsViewModel>(App.Services);
        Sidebar = ActivatorUtilities.CreateInstance<SidebarSettingsViewModel>(App.Services);
        InitializeComponent();
        Loaded += OnFirstLoaded;
    }

    public PosterSettingsViewModel Posters { get; }

    public BadgeSettingsViewModel ViewModel { get; }

    public SidebarSettingsViewModel Sidebar { get; }

    public void Dispose()
    {
        Posters.Dispose();
        ViewModel.Dispose();
        Sidebar.Dispose();
    }

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        await SettingsViewLoad.RunAsync(_ => Task.WhenAll(Posters.LoadAsync(), ViewModel.LoadAsync()));
    }

    // Reset clears every badge choice of the profile, so it asks first, as the web does.
    private async void OnResetBadgesClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = BadgeSettingsViewModel.ResetTitle,
            Content = BadgeSettingsViewModel.ResetText,
            PrimaryButtonText = "Reset badges",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ResetCommand.ExecuteAsync(null);
        }
    }
}

/// <summary>Settings → Servers; the page disposes it as it leaves, so the list stops following the servers.</summary>
public sealed partial class ServersSettingsView : UserControl, IDisposable
{
    public ServersSettingsView()
    {
        // Created outside the container: the root provider would keep every disposable transient alive.
        ViewModel = ActivatorUtilities.CreateInstance<ServersViewModel>(App.Services);
        InitializeComponent();
    }

    public ServersViewModel ViewModel { get; }

    public void Dispose() => ViewModel.Dispose();

    private async void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        if (ServerDialogs.ItemOf(sender) is { } server)
        {
            await ViewModel.SwitchCommand.ExecuteAsync(server);
        }
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (ServerDialogs.ItemOf(sender) is { } server)
        {
            await ViewModel.SignOutCommand.ExecuteAsync(server);
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ServerDialogs.ItemOf(sender) is { } server && await ServerDialogs.ConfirmRemoveAsync(XamlRoot, server))
        {
            await ViewModel.RemoveCommand.ExecuteAsync(server);
        }
    }
}

/// <summary>Settings → Devices; it reads the account's sign-ins and the profile's devices when it is shown.</summary>
public sealed partial class DevicesSettingsView : UserControl
{
    private static readonly SolidColorBrush ActiveBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x34, 0xD3, 0x99));

    public DevicesSettingsView()
    {
        ViewModel = ActivatorUtilities.CreateInstance<DevicesViewModel>(App.Services);
        InitializeComponent();
        // Once: the page keeps the view, and Refresh reads the lists again.
        Loaded += OnFirstLoaded;
    }

    public DevicesViewModel ViewModel { get; }

    // Static because x:Bind function bindings in the templates call them on the type.
    public static string SessionGlyph(SessionDeviceKind kind) => kind switch
    {
        SessionDeviceKind.Tv => "",
        SessionDeviceKind.Phone => "",
        SessionDeviceKind.Desktop => "",
        _ => "",
    };

    /// <summary>The icon of a device by its kind: TV, Tablet, Mobile, Desktop or Other.</summary>
    public static string DeviceGlyph(string kind) => kind switch
    {
        "TV" => "",
        "Tablet" => "",
        "Mobile" => "",
        "Desktop" => "",
        _ => "",
    };

    public static string Chevron(bool expanded) => expanded ? "" : "";

    /// <summary>"Active now" in green, as the web shows a sign-in seen in the last two minutes.</summary>
    public static Brush SeenBrush(bool activeNow) => activeNow ? ActiveBrush : (Brush)Application.Current.Resources["SiloMutedBrush"];

    public static string Upper(string text) => text.ToUpperInvariant();

    private async void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        await SettingsViewLoad.RunAsync(ViewModel.LoadAsync);
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SignedInSessionRow row })
        {
            await ViewModel.SignOutSessionCommand.ExecuteAsync(row);
        }
    }

    private async void OnSaveRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UserDeviceRow row })
        {
            await ViewModel.SaveRenameCommand.ExecuteAsync(row);
        }
    }

    private async void OnUseProfileSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UserDeviceRow row })
        {
            await ViewModel.UseProfileSettingsCommand.ExecuteAsync(row);
        }
    }

    private async void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UserDeviceRow row })
        {
            await ViewModel.ForgetCommand.ExecuteAsync(row);
        }
    }
}

/// <summary>Settings → About; the page detaches it as it leaves, so the app's updates let go of it.</summary>
public sealed partial class AboutSettingsView : UserControl, IDisposable
{
    public AboutSettingsView()
    {
        ViewModel = ActivatorUtilities.CreateInstance<AboutViewModel>(App.Services);
        InitializeComponent();
    }

    public AboutViewModel ViewModel { get; }

    public void Dispose() => ViewModel.Detach();

    private void OnViewLicenseClick(object sender, RoutedEventArgs e) => OpenText(ViewModel.LicensePath);

    private void OnViewNoticesClick(object sender, RoutedEventArgs e) => OpenText(ViewModel.NoticesPath);

    private void OnOpenDataClick(object sender, RoutedEventArgs e) => OpenFolder(ViewModel.DataDirectory);

    // The logs folder is made with the first log line; before that, its parent.
    private void OnOpenLogsClick(object sender, RoutedEventArgs e) =>
        OpenFolder(Directory.Exists(ViewModel.LogsDirectory) ? ViewModel.LogsDirectory : ViewModel.DataDirectory);

    // Notepad: the licence has no extension, and Windows knows no app for .md by default. A missing file is not offered to
    // be made, nor a missing folder swapped for Documents.
    private static void OpenText(string path)
    {
        if (File.Exists(path))
        {
            Start("notepad.exe", path);
        }
        else
        {
            Logger().LogWarning("{File} is missing next to Granary", Path.GetFileName(path));
        }
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Start("explorer.exe", path);
        }
        else
        {
            Logger().LogWarning("Granary's folder {Folder} does not exist yet", path);
        }
    }

    private static ILogger Logger() => App.Services.GetRequiredService<ILoggerFactory>().CreateLogger<AboutSettingsView>();

    private static void Start(string program, string path)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Logger().LogWarning(ex, "Could not open {Program}", program);
        }
    }
}

/// <summary>What the servers list and Settings → Servers share: the server a control stands for, and the question before removing one.</summary>
internal static class ServerDialogs
{
    public static ServerItemViewModel? ItemOf(object sender) => sender switch
    {
        FrameworkElement { Tag: ServerItemViewModel tagged } => tagged,
        FrameworkElement { DataContext: ServerItemViewModel context } => context,
        _ => null,
    };

    public static async Task<bool> ConfirmRemoveAsync(XamlRoot? root, ServerItemViewModel server)
    {
        if (root is null)
        {
            return false;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = $"Remove {server.Name}?",
            Content = "Granary signs out of this server and forgets it. You can add it again later.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>A settings view's first read: the page says what went wrong itself, and nothing escapes the event handler.</summary>
internal static class SettingsViewLoad
{
    public static async Task RunAsync(Func<CancellationToken, Task> load)
    {
        try
        {
            await load(CancellationToken.None);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SettingsViewLoad)).LogError(ex, "A settings page could not load");
        }
    }
}
