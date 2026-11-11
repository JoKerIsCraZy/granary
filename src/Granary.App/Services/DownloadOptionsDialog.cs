using System.ComponentModel;
using System.Runtime.InteropServices;
using Granary.App.Controls;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Services;

/// <summary>
/// The download dialog over the main window: the web's <c>components/DownloadVersionPicker.tsx</c> with its title and texts,
/// and Granary's qualities and free space (<see cref="DownloadOptionsPanel"/>). Download stays pressable while the selection
/// fits; the dialog stays open while the start runs and when it did not start, which the panel says, and closes once it did.
/// Cancel closes it. One at a time, as <see cref="DialogConfirmation"/> asks.
/// </summary>
internal sealed class DownloadOptionsDialog(IServiceProvider services, ILogger<DownloadOptionsDialog> logger) : IDownloadOptionsDialog
{
    private bool _showing;

    public async Task ShowAsync(DownloadOptionsViewModel options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // The window is asked for when the dialog opens: it is made after the services.
        if (_showing || services.GetRequiredService<MainWindow>().Content?.XamlRoot is not { } root)
        {
            return;
        }
        _showing = true;
        ContentDialog? dialog = null;
        void FollowCanStart(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DownloadOptionsViewModel.CanStart) && dialog is not null)
            {
                dialog.IsPrimaryButtonEnabled = options.CanStart;
            }
        }
        // Download starts what the dialog shows; a start that did not happen keeps it open with the reason.
        async void OnDownloadClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Cancel = !await options.StartAsync();
            }
            catch (Exception ex)
            {
                // async void: nothing may escape. The dialog stays, so Download can be pressed again.
                logger.LogError("The download could not be started ({Failure})", ex.GetType().Name);
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        }
        options.PropertyChanged += FollowCanStart;
        try
        {
            dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = options.Title,
                Content = new DownloadOptionsPanel(options),
                PrimaryButtonText = "Download",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = options.CanStart,
            };
            dialog.PrimaryButtonClick += OnDownloadClick;
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Another dialog is open (a PIN, a question): nothing starts.
            logger.LogWarning("The download dialog could not be shown: another dialog is open ({Failure})", ex.GetType().Name);
        }
        finally
        {
            options.PropertyChanged -= FollowCanStart;
            if (dialog is not null)
            {
                dialog.PrimaryButtonClick -= OnDownloadClick;
            }
            _showing = false;
        }
    }
}
