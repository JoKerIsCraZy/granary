using System.Globalization;
using System.Text.RegularExpressions;

namespace Granary.Tests.Admin;

/// <summary>
/// The admin pages' green, blue and amber (the play methods, a limited bitrate, a warning) are the web's status tokens,
/// <c>--success</c>, <c>--info</c> and <c>--warning</c> in silo-server's <c>web/src/app.css</c>, which the web writes in OKLCH:
/// <c>Themes/Silo.xaml</c> holds them as sRGB.
/// </summary>
public sealed class StatusColourTests
{
    [Theory]
    [InlineData("SiloSuccessColor", 0.75, 0.18, 145)]
    [InlineData("SiloInfoColor", 0.7, 0.15, 250)]
    [InlineData("SiloWarningColor", 0.8, 0.15, 85)]
    public void TheStatusColours_AreTheWebsTokens(string key, double lightness, double chroma, double hue)
    {
        string theme = File.ReadAllText(Path.Combine(RepositoryFiles.Root, "src", "Granary.App", "Themes", "Silo.xaml"));
        var match = ColourOf(key).Match(theme);

        Assert.True(match.Success, $"{key} was not found in Silo.xaml");
        Assert.Equal(Srgb(lightness, chroma, hue), match.Groups[1].Value, ignoreCase: true);
    }

    // CSS Color 4's OKLCH to sRGB (Björn Ottosson's OKLab matrices), as a browser paints the web's token.
    private static string Srgb(double lightness, double chroma, double hue)
    {
        double a = chroma * Math.Cos(hue * Math.PI / 180);
        double b = chroma * Math.Sin(hue * Math.PI / 180);
        double l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
        double m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s = Math.Pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3);
        double[] linear =
        [
            4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s,
        ];
        return "#FF" + string.Concat(linear.Select(channel => Encode(channel).ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static int Encode(double linear)
    {
        double clamped = Math.Clamp(linear, 0, 1);
        double encoded = clamped <= 0.0031308 ? 12.92 * clamped : 1.055 * Math.Pow(clamped, 1 / 2.4) - 0.055;
        return (int)Math.Round(encoded * 255, MidpointRounding.AwayFromZero);
    }

    private static Regex ColourOf(string key) => new($$"""<Color x:Key="{{key}}">(#[0-9A-Fa-f]{8})</Color>""");
}
