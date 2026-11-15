using System.Globalization;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// <see cref="IDeviceLanguages"/> from Windows: the preferred languages (<c>GlobalizationPreferences.Languages</c>, as the
/// user ordered them in Settings → Time &amp; language) and the regional format .NET took for <see cref="CultureInfo.CurrentCulture"/>,
/// both read once, as Granary starts, which the texts of the whole run follow.
/// </summary>
internal sealed class WindowsDeviceLanguages : IDeviceLanguages
{
    public WindowsDeviceLanguages(ILogger<WindowsDeviceLanguages> logger)
    {
        RegionalFormat = CultureInfo.CurrentCulture;
        try
        {
            Preferred = [.. global::Windows.System.UserProfile.GlobalizationPreferences.Languages];
        }
        catch (Exception ex)
        {
            // Without them Granary shows English, as it would for a language it does not ship.
            logger.LogWarning(ex, "Windows' languages could not be read");
            Preferred = [];
        }
    }

    public IReadOnlyList<string> Preferred { get; }

    public CultureInfo RegionalFormat { get; }
}
