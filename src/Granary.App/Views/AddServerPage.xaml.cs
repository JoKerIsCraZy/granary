using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Views;

public sealed partial class AddServerPage : Page
{
    public AddServerPage()
    {
        ViewModel = App.Services.GetRequiredService<AddServerViewModel>();
        InitializeComponent();
    }

    public AddServerViewModel ViewModel { get; }
}
