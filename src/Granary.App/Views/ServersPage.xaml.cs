using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Views;

public sealed partial class ServersPage : Page
{
    public ServersPage()
    {
        // Created outside the container: the root provider would keep every disposable transient alive.
        ViewModel = ActivatorUtilities.CreateInstance<ServersViewModel>(App.Services);
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public ServersViewModel ViewModel { get; }

    private async void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        if (Settings.ServerDialogs.ItemOf(sender) is { } server)
        {
            await ViewModel.SwitchCommand.ExecuteAsync(server);
        }
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (Settings.ServerDialogs.ItemOf(sender) is { } server)
        {
            await ViewModel.SignOutCommand.ExecuteAsync(server);
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Settings.ServerDialogs.ItemOf(sender) is { } server && await Settings.ServerDialogs.ConfirmRemoveAsync(XamlRoot, server))
        {
            await ViewModel.RemoveCommand.ExecuteAsync(server);
        }
    }
}
