using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;

namespace Granary.App.Services;

/// <summary>
/// Copies a bearer secret on request: a Watch Party's code or invite link, an invitation's link, a password reset link, an
/// invite code. It goes to the clipboard only, never to a log, nor to the clipboard's history or other devices.
/// </summary>
internal static class SecretClipboard
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
            App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SecretClipboard))
                .LogInformation("The clipboard could not be written: {Failure}", ex.GetType().Name);
            return false;
        }
    }
}
