using Granary.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Storage.Pickers;

namespace Granary.App.Services;

/// <summary>
/// Windows' folder picker for Settings → Downloads' Change, over the main window, starting in Downloads. Null when the viewer
/// cancelled, and when the picker could not open, which is logged by its kind.
/// </summary>
public sealed class WindowsFolderPicker(IServiceProvider services, ILogger<WindowsFolderPicker> logger) : IFolderPicker
{
    public async Task<string?> PickFolderAsync()
    {
        try
        {
            // The window is asked for when the picker opens: it is made after the services.
            var window = services.GetRequiredService<MainWindow>();
            var picker = new FolderPicker(window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.Downloads };
            return (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception ex)
        {
            // Whatever Windows says, Settings stays usable; the folder stays as it was.
            logger.LogWarning("The folder picker could not open ({Failure})", ex.GetType().Name);
            return null;
        }
    }
}
