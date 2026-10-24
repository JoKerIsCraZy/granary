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
        if (ItemOf(sender) is { } server)
        {
            await ViewModel.SwitchCommand.ExecuteAsync(server);
        }
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } server)
        {
            await ViewModel.SignOutCommand.ExecuteAsync(server);
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } server || XamlRoot is null)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = $"Remove {server.Name}?",
            Content = "Granary signs out of this server and forgets it. You can add it again later.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveCommand.ExecuteAsync(server);
        }
    }

    private static ServerItemViewModel? ItemOf(object sender) => sender switch
    {
        FrameworkElement { Tag: ServerItemViewModel tagged } => tagged,
        FrameworkElement { DataContext: ServerItemViewModel context } => context,
        _ => null,
    };
}
