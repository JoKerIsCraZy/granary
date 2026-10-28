using Granary.Core.ViewModels.WatchParty;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Granary.App.Views;

/// <summary>The Watch Party hub.</summary>
public sealed partial class WatchPartyPage : Page
{
    private const double ColumnWidth = 896;

    public WatchPartyPage()
    {
        ViewModel = App.Services.GetRequiredService<WatchPartyHubViewModel>();
        InitializeComponent();
    }

    public WatchPartyHubViewModel ViewModel { get; }

    // A column capped by MaxWidth in a ScrollViewer ends up off centre, so its width is set here.
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => Column.Width = Math.Min(ColumnWidth, e.NewSize.Width);

    private void OnCodeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.JoinCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.JoinCommand.Execute(null);
        }
    }

    // Paste takes the clipboard's text as it is: a code, or an invite link the join reads the token from.
    private async void OnPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                ViewModel.Code = (await content.GetTextAsync()).Trim();
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A refused clipboard leaves the box to typing; the text is never logged.
            App.Services.GetRequiredService<ILogger<WatchPartyPage>>().LogInformation("The clipboard could not be read: {Failure}", ex.GetType().Name);
        }
    }
}
