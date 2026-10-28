using Granary.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Services;

/// <summary>The web's confirm dialog: the question, what it does, the action and Cancel (the default).</summary>
internal sealed class DialogConfirmation(IServiceProvider services) : IConfirmation
{
    private bool _asking;

    public async Task<bool> AskAsync(string title, string message, string action)
    {
        // One question at a time: a second one while the first is open would throw.
        if (_asking || services.GetRequiredService<MainWindow>().Content?.XamlRoot is not { } root)
        {
            return false;
        }
        _asking = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = action,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _asking = false;
        }
    }
}
