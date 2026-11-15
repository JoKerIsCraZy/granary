using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App;

// Back at the root, on Home's entry in the sidebar, asks whether Granary closes (BackLadder.AskToClose): Silo's TV apps
// leave there, and a remote or a controller has no other way to close the window. Cancel is the default and has focus,
// so a stray A or Enter closes nothing; B, Esc and the remote's Back cancel, as they close any dialog. Close goes the
// way the title bar's Close goes, so the player's stop and what else the window saves as it closes come first.
public sealed partial class MainWindow
{
    // The button ContentDialog's template names Cancel's place.
    private const string DialogCloseButtonName = "CloseButton";

    private bool _isAskingToClose;

    // One question at a time, and none over another dialog or menu: Back closes that first (TakePadInPopup,
    // OnPopupKeyDown), but a second Back can come before the dialog's popup is open.
    private async void AskToClose()
    {
        if (_isAskingToClose || OpenPopup() is not null || Root.XamlRoot is not { } root)
        {
            return;
        }
        _isAskingToClose = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                Title = "Close Granary?",
                PrimaryButtonText = "Close",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            // As the keyboard's focus, so its ring shows for a remote and a controller.
            dialog.Opened += (sender, _) => FindNamed<Button>(sender, DialogCloseButtonName)?.Focus(FocusState.Keyboard);
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                CloseAsWindowsDoes();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Another dialog opened meanwhile: nothing closes without the answer.
            _logger.LogWarning(ex, "Could not ask whether Granary closes: another dialog is open");
        }
        finally
        {
            _isAskingToClose = false;
        }
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if ((child is T match && match.Name == name ? match : FindNamed<T>(child, name)) is { } element)
            {
                return element;
            }
        }
        return null;
    }
}
