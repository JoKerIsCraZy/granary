using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public string Greeting(string profileName) => profileName.Length > 0 ? $"Welcome back, {profileName}" : "Welcome back";

    public string Signature(string userName, string serverName) =>
        userName.Length > 0 ? $"Signed in as {userName} on {serverName}" : serverName;
}
