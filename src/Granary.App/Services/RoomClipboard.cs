using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;

namespace Granary.App.Services;

/// <summary>
/// Copies a Watch Party's code or invite link, for the room page and the player's panel. They are secrets of the room:
/// they go to the clipboard only, never to a log, nor to the clipboard's history or other devices.
/// </summary>
internal static class RoomClipboard
{
    /// <summary>False when the clipboard could not be written.</summary>
    public static bool Copy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            return Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RoomClipboard))
                .LogInformation("The clipboard could not be written: {Failure}", ex.GetType().Name);
            return false;
        }
    }
}
