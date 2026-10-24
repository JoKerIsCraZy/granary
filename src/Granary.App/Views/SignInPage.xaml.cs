using System.ComponentModel;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Granary.App.Views;

/// <summary>Password form beside device (QR) sign-in for one saved server; the parameter is its entry id.</summary>
public sealed partial class SignInPage : Page
{
    private bool _deviceSignInStarted;

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
        DeviceSignIn.Stop();
        base.OnNavigatedFrom(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SignInViewModel.CanUseCredentials))
        {
            StartWhenAllowed();
        }
    }

    // Device sign-in also brings tokens over the connection, so it waits for the same consent as the password form.
    // Once per visit: both the consent and the end of loading lead here, and a second start would replace the
    // first run, cancelling its request in flight.
    private void StartWhenAllowed()
    {
        if (!_deviceSignInStarted && ViewModel.CanUseCredentials && ViewModel.Server is { } server)
        {
            _deviceSignInStarted = true;
            DeviceSignIn.Start(server);
            UsernameBox.Focus(FocusState.Programmatic);
        }
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

    public bool HasText(string text) => text.Length > 0;

    public bool NeedsConsent(bool isUnencrypted, bool accepted) => isUnencrypted && !accepted;
}
