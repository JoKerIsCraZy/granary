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
    }

    public AddServerViewModel ViewModel { get; }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.ConnectCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.ConnectCommand.Execute(null);
        }
    }
}
