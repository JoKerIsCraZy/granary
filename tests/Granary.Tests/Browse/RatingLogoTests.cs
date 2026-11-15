using System.Xml.Linq;

namespace Granary.Tests.Browse;

/// <summary>
/// TMDB's logo beside a TMDB score is drawn by Windows' SVG renderer (Direct2D, behind SvgImageSource), which ignores a
/// &lt;style&gt; stylesheet and the class attribute: a shape styled only through them is drawn in the default black, which
/// is invisible on the hero's dark backdrop. So every shape carries its paint as an attribute.
/// </summary>
public sealed class RatingLogoTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static XDocument TmdbLogo() =>
        XDocument.Load(Path.Combine(RepositoryFiles.Root, "src", "Granary.App", "Assets", "Ratings", "tmdb-logo.svg"));

    [Fact]
    public void TheTmdbLogo_HasNoStylesheet_NorClasses_WhichWindowsDoesNotRead()
    {
        var logo = TmdbLogo();

        Assert.Empty(logo.Descendants(Svg + "style"));
        Assert.DoesNotContain(logo.Descendants(), e => e.Attribute("class") is not null);
    }

    [Fact]
    public void EveryShapeOfTheTmdbLogo_IsPaintedWithItsGradient_InTmdbsColours()
    {
        var logo = TmdbLogo();
        var gradientElement = Assert.Single(logo.Descendants(Svg + "linearGradient"));
        string gradient = gradientElement.Attribute("id")!.Value;
        // TMDB's green to blue, each stop's colour as an attribute, which Windows reads.
        Assert.Equal(["#90cea1", "#3cbec9", "#00b3e5"], gradientElement.Elements(Svg + "stop").Select(s => s.Attribute("stop-color")?.Value));

        var shapes = logo.Descendants(Svg + "path").ToList();
        Assert.NotEmpty(shapes);
        Assert.All(shapes, shape => Assert.Equal($"url(#{gradient})", shape.Attribute("fill")?.Value));
    }
}
