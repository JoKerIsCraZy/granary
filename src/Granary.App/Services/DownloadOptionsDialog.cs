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
/// and Granary's qualities and free space (<see cref="DownloadOptionsPanel"/>). Download stays pressable while there is
/// something to download and the selection fits (off while a card's dialog reads the title's files); the dialog stays open
/// while the start runs and when it did not start, which the panel says, and closes once it did. Cancel closes it. One at a
/// time, as <see cref="DialogConfirmation"/> asks.
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
        // Download follows whether it can start; a card's dialog gets its title once it has read the title's files.
        void FollowOptions(object? sender, PropertyChangedEventArgs e)
        {
            if (dialog is null)
            {
                return;
            }
            if (e.PropertyName == nameof(DownloadOptionsViewModel.CanStart))
            {
                dialog.IsPrimaryButtonEnabled = options.CanStart;
            }
            else if (e.PropertyName == nameof(DownloadOptionsViewModel.Title))
            {
                dialog.Title = options.Title;
            }
        }
        // Download starts what the dialog shows; a start that did not happen keeps it open with the reason.
        async void OnDownloadClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            bool keepOpen = true;
            try
            {
                keepOpen = !await options.StartAsync();
            }
            catch (Exception ex)
            {
                // async void: nothing may escape. The dialog stays, so Download can be pressed again.
                logger.LogError("The download could not be started ({Failure})", ex.GetType().Name);
            }
            try
            {
                args.Cancel = keepOpen;
                deferral.Complete();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                // The dialog went meanwhile, with the window: there is nothing left to keep open.
                logger.LogDebug("The download dialog was gone as its start ended ({Failure})", ex.GetType().Name);
            }
        }
        // A start under way finishes before the dialog goes: Cancel or Esc then would close it with the download starting
        // all the same, and nothing saying so.
        void HoldWhileStarting(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (options.IsStarting && args.Result == ContentDialogResult.None)
            {
                args.Cancel = true;
            }
        }
        options.PropertyChanged += FollowOptions;
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
            dialog.Closing += HoldWhileStarting;
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Most often another dialog is open (a PIN, a question); nothing starts either way.
            logger.LogWarning("The download dialog could not be shown ({Failure})", ex.GetType().Name);
        }
        finally
        {
            options.PropertyChanged -= FollowOptions;
            if (dialog is not null)
            {
                dialog.PrimaryButtonClick -= OnDownloadClick;
                dialog.Closing -= HoldWhileStarting;
            }
            _showing = false;
        }
    }
}
