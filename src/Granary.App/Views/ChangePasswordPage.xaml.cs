using System.ComponentModel;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Granary.App.Views;

public sealed partial class ChangePasswordPage : Page
{
    private string _announcedStatus = "";

    public ChangePasswordPage()
    {
        ViewModel = App.Services.GetRequiredService<ChangePasswordViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => CurrentPasswordBox.Focus(FocusState.Programmatic);
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public ChangePasswordViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    private void OnConfirmPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.SubmitCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.SubmitCommand.Execute(null);
        }
    }

    // An InfoBar announces itself when it opens; a new message in an open bar is announced here.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChangePasswordViewModel.Status))
        {
            return;
        }
        string status = ViewModel.Status;
        if (_announcedStatus.Length > 0 && status.Length > 0 && status != _announcedStatus)
        {
            var peer = FrameworkElementAutomationPeer.FromElement(StatusBar) ?? FrameworkElementAutomationPeer.CreatePeerForElement(StatusBar);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
                status, "PasswordStatus");
        }
        _announcedStatus = status;
    }

    public static bool HasText(string text) => !string.IsNullOrEmpty(text);

    public static InfoBarSeverity StatusSeverity(bool hasError) => hasError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
}
