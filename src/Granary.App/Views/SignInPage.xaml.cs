using System.ComponentModel;
using Granary.App.Controls;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Granary.App.Views;

/// <summary>Password form above device (QR) sign-in for one saved server; the parameter is its entry id.</summary>
public sealed partial class SignInPage : Page
{
    private bool _deviceSignInStarted;
    private bool _formFocused;

    public SignInPage()
    {
        ViewModel = App.Services.GetRequiredService<SignInViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public SignInViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync(e.Parameter as string);
        StartWhenAllowed();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _deviceSignInStarted = false;
        _formFocused = false;
        DeviceSignIn.SizeChanged -= OnDeviceSignInSizeChanged;
        DeviceSignIn.Stop();
        DeviceSignIn.Visibility = Visibility.Collapsed;
        ShowCodeButton.Visibility = Visibility.Visible;
        base.OnNavigatedFrom(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SignInViewModel.CanUseCredentials) or nameof(SignInViewModel.IsPasswordSignInAvailable))
        {
            StartWhenAllowed();
        }
        else if (e.PropertyName == nameof(SignInViewModel.Status))
        {
            // The refusal's box appears with it, too late for a live region.
            Announcer.Announce(this, ViewModel.Status, "SignInStatus");
        }
    }

    // Both forms wait for the consent to an unencrypted connection. As on Silo's web, the code shows when asked for
    // ("Show QR code"); a server without password sign-in has only the code, so it shows at once.
    private void StartWhenAllowed()
    {
        if (!ViewModel.CanUseCredentials)
        {
            return;
        }
        if (!ViewModel.IsPasswordSignInAvailable)
        {
            StartDeviceSignIn();
        }
        else if (!_formFocused)
        {
            // Queued: the form's bindings show it after this handler, and a collapsed box takes no focus.
            _formFocused = true;
            DispatcherQueue.TryEnqueue(() => UsernameBox.Focus(FocusState.Programmatic));
        }
    }

    // The button goes away, so its focus moves on to the code: keyboard and screen reader users stay in place.
    private void OnShowCodeClick(object sender, RoutedEventArgs e)
    {
        var focus = ShowCodeButton.FocusState;
        StartDeviceSignIn();
        DispatcherQueue.TryEnqueue(() => DeviceSignIn.Focus(focus == FocusState.Unfocused ? FocusState.Programmatic : focus));
    }

    // Device sign-in also brings tokens over the connection, so it needs the same consent. Once per visit: a second
    // start would replace the first run, cancelling its request in flight.
    private void StartDeviceSignIn()
    {
        if (!_deviceSignInStarted && ViewModel.CanUseCredentials && ViewModel.Server is { } server)
        {
            _deviceSignInStarted = true;
            ShowCodeButton.Visibility = Visibility.Collapsed;
            DeviceSignIn.Visibility = Visibility.Visible;
            DeviceSignIn.SizeChanged += OnDeviceSignInSizeChanged;
            DeviceSignIn.Start(server);
        }
    }

    // The code opens below the form, mostly past the window's edge: scroll to it as it grows into place, until its
    // texts have come (they arrive a moment after the frame) or the run has ended without a code. Later changes must
    // not move a page being typed in.
    private void OnDeviceSignInSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DeviceSignIn.ViewModel.HasCode || !DeviceSignIn.ViewModel.IsActive)
        {
            DeviceSignIn.SizeChanged -= OnDeviceSignInSizeChanged;
        }
        DeviceSignIn.StartBringIntoView();
    }

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.SignInCommand.CanExecute(null))
        {
            ViewModel.SignInCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public string Heading(ServerEntry? server) => server is null ? "Sign in" : $"Sign in to {server.Name}";

    // The web shows the server's name as the card's title; screen readers hear Heading.
    public string ServerName(ServerEntry? server) => server?.Name ?? "Sign in";

    public string SignInLabel(bool isRunning) => isRunning ? "Signing in…" : "Sign in";

    public string DeviceHeading(bool isPasswordSignInAvailable) =>
        isPasswordSignInAvailable ? "Use your phone instead" : "Sign in with your phone";

    public Visibility ShowsPasswordForm(bool canUseCredentials, bool isPasswordSignInAvailable) =>
        canUseCredentials && isPasswordSignInAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NeedsConsent(bool isUnencrypted, bool accepted) => isUnencrypted && !accepted ? Visibility.Visible : Visibility.Collapsed;
}
