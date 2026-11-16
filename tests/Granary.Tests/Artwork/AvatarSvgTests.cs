using System.Text;
using Granary.Core.Artwork;

namespace Granary.Tests.Artwork;

/// <summary>
/// Which of the server's preset avatars are drawn as SVG: only markup the server labels <c>image/svg+xml</c>, whose root is
/// <c>&lt;svg&gt;</c>, small, and with nothing that reaches outside itself or declares entities. Anything else that claims to be
/// SVG is refused, and the avatar shows its letter.
/// </summary>
public sealed class AvatarSvgTests
{
    private const string SvgType = "image/svg+xml";

    // As the server's own presets (web/public/profile-avatars/avatar-1.svg).
    private const string Preset = """
        <svg width="256" height="256" viewBox="0 0 256 256" fill="none" xmlns="http://www.w3.org/2000/svg">
          <rect width="256" height="256" rx="72" fill="#1D4ED8"/>
          <circle cx="128" cy="136" r="66" fill="#F8FAFC"/>
        </svg>
        """;

    // As DiceBear draws the server's dicebear: presets (pixel-art-neutral, cut down): metadata naming its licence's web pages as
    // text, a mask and a gradient referred to by #id.
    private const string DiceBearPreset = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 14 14" fill="none" shape-rendering="crispEdges" width="128" height="128"><metadata xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:dc="http://purl.org/dc/elements/1.1/"><rdf:RDF><rdf:Description><dc:title>Pixel Art Neutral</dc:title><dc:source>https://www.figma.com/community/file/1</dc:source></rdf:Description></rdf:RDF></metadata><mask id="viewboxMask"><rect width="14" height="14" rx="3.36" ry="3.36" fill="#fff" /></mask><g mask="url(#viewboxMask)"><rect fill="url(#backgroundLinear)" width="14" height="14" /><defs><linearGradient id="backgroundLinear" gradientTransform="rotate(358 0.5 0.5)"><stop stop-color="#eac393"/><stop offset="1" stop-color="#f5cfa0"/></linearGradient></defs><path d="M5 6h1v2H4V7h1V6Z" fill="#5b7c8b"/></g></svg>
        """;

    private static ArtworkBytes Image(string markup, string? mediaType = SvgType) => Image(Encoding.UTF8.GetBytes(markup), mediaType);

    private static ArtworkBytes Image(byte[] bytes, string? mediaType)
    {
        var image = ArtworkBytes.From(bytes);
        image.MediaType = mediaType;
        return image;
    }

    [Theory]
    [InlineData(Preset)]
    [InlineData(DiceBearPreset)]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"4\"/></svg>")]
    [InlineData("\uFEFF \r\n\t<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><defs><linearGradient id=\"g\"/></defs><rect fill=\"url(#g)\"/><use href=\"#g\"/><use xlink:href='#g'/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><use xlink:href = \"#g\"/><use href= '#g'/><rect fill=\"url( '#g')\"/><rect fill=\"url( #g )\"/></svg>")]
    public void APresetFromTheServer_IsDrawn(string markup)
    {
        Assert.Equal(AvatarSvgCheck.Drawable, AvatarSvg.Check(Image(markup)));
    }

    // A bitmap goes to the bitmap decoder, as do bytes the server did not say are SVG: guessed from the content, they could be anything.
    [Theory]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x24, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', (byte)'/', (byte)'>' }, null)]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', (byte)'/', (byte)'>' }, "application/octet-stream")]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', (byte)'/', (byte)'>' }, "image/png")]
    public void AnythingNotLabelledSvg_IsNoSvg(byte[] bytes, string? mediaType)
    {
        Assert.Equal(AvatarSvgCheck.NotSvg, AvatarSvg.Check(Image(bytes, mediaType)));
    }

    [Theory]
    [InlineData("<html><body><svg/></body></html>")]
    [InlineData("<!-- a note --><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<svgx/>")]
    [InlineData("not markup at all")]
    [InlineData("")]
    public void ARootOtherThanSvg_IsRefused(string markup)
    {
        Assert.Equal(AvatarSvgCheck.Refused, AvatarSvg.Check(Image(markup)));
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaa\">]><svg>&a;</svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><!ENTITY a SYSTEM \"file:///C:/secret\"></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><!doctype svg></svg>")]
    public void DeclaredEntitiesOrADoctype_AreRefused(string markup)
    {
        Assert.Equal(AvatarSvgCheck.Refused, AvatarSvg.Check(Image(markup)));
    }

    // Nothing in the drawing may be fetched from elsewhere: only references inside it (#id) are drawn.
    [Theory]
    [InlineData("<svg><image href=\"https://elsewhere.example/a.png\"/></svg>")]
    [InlineData("<svg><image xlink:href='//elsewhere.example/a.png'/></svg>")]
    [InlineData("<svg><image href=\"data:image/png;base64,AAAA\"/></svg>")]
    [InlineData("<svg><use href = \"other.svg#a\"/></svg>")]
    [InlineData("<svg><rect fill=\"url(https://elsewhere.example/p.svg#g)\"/></svg>")]
    [InlineData("<svg><rect style=\"fill: url( 'http://elsewhere.example/g' )\"/></svg>")]
    [InlineData("<svg><style>@import \"https://elsewhere.example/a.css\";</style></svg>")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<svg><foreignObject><div/></foreignObject></svg>")]
    public void AReferenceOutsideTheDrawing_IsRefused(string markup)
    {
        Assert.Equal(AvatarSvgCheck.Refused, AvatarSvg.Check(Image(markup)));
    }

    // Direct2D draws no text: DiceBear's initials style would show its gradient without the letters, so the avatar's own letter
    // shows instead.
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect fill=\"#5b7c8b\"/><text x=\"50%\" y=\"50%\" fill=\"#fff\">EM</text></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><TEXT>E</TEXT></svg>")]
    public void ADrawingWithText_IsRefused(string markup)
    {
        Assert.Equal(AvatarSvgCheck.Refused, AvatarSvg.Check(Image(markup)));
    }

    [Fact]
    public void MarkupLargerThanAnAvatarNeeds_IsRefused()
    {
        string large = "<svg xmlns=\"http://www.w3.org/2000/svg\">" + new string(' ', AvatarSvg.MaxBytes) + "</svg>";

        Assert.Equal(AvatarSvgCheck.Refused, AvatarSvg.Check(Image(large)));
    }

    [Fact]
    public void MarkupThatIsNotUtf8_IsRefused()
    {
        byte[] latin1 = [.. "<svg><text>"u8, 0xE9, .. "</text></svg>"u8];

        Assert.Equal(AvatarSvgCheck.Refused, AvatarSvg.Check(Image(latin1, SvgType)));
    }

    [Fact]
    public void TheMediaType_IsReadWithoutRegardToCase()
    {
        Assert.Equal(AvatarSvgCheck.Drawable, AvatarSvg.Check(Image(Preset, "Image/SVG+XML")));
    }
}
