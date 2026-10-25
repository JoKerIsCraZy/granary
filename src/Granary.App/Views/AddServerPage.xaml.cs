using System.ComponentModel;
using Granary.App.Controls;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Granary.App.Views;

public sealed partial class AddServerPage : Page
{
    public AddServerPage()
    {
        ViewModel = App.Services.GetRequiredService<AddServerViewModel>();
        InitializeComponent();
        Loaded += (_, _) => AddressBox.Focus(FocusState.Programmatic);
        // A connect still running when the user leaves must not navigate to sign-in later.
        Unloaded += (_, _) => ViewModel.ConnectCommand.Cancel();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public AddServerViewModel ViewModel { get; }

    // Instance methods because x:Bind function bindings call them on the page.
    public string ConnectLabel(bool isRunning) => isRunning ? "Connecting…" : "Connect";

    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    // Progress and refusals are read out; the line that shows them is hidden while there is none.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddServerViewModel.Status))
        {
            Announcer.Announce(this, ViewModel.Status, "AddServerStatus");
        }
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.ConnectCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.ConnectCommand.Execute(null);
        }
    }
}
