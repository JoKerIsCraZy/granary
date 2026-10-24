using Granary.Core.Api.Images;

namespace Granary.Tests.Catalog;

public sealed class ArtworkUrlTests
{
    private static readonly Uri PrefixedBase = new("https://nas.example/silo");
    private static readonly Uri RootBase = new("http://192.168.1.20:8090");

    [Theory]
    [InlineData("https://cdn.example/posters/heat.webp?X-Amz-Signature=a%2Bb", "https://cdn.example/posters/heat.webp?X-Amz-Signature=a%2Bb")]
    [InlineData("http://images.example/p.jpg", "http://images.example/p.jpg")]
    [InlineData("/api/v2/artwork/movies/949/poster/w342.ab12.webp?exp=1767225600&sig=a-b_c",
        "https://nas.example/silo/api/v2/artwork/movies/949/poster/w342.ab12.webp?exp=1767225600&sig=a-b_c")]
    [InlineData("/avatars/presets/fox.png", "https://nas.example/silo/avatars/presets/fox.png")]
    public void Resolve_KeepsAbsoluteUrlsAndAppendsRootRelativeOnesToThePrefixedBase(string value, string expected)
    {
        Assert.Equal(expected, ArtworkUrl.Resolve(PrefixedBase, value)?.AbsoluteUri);
    }

    [Fact]
    public void Resolve_UsesTheOriginWhenTheBaseHasNoPrefix()
    {
        Assert.Equal("http://192.168.1.20:8090/api/v2/artwork/k.webp?exp=1&sig=s",
            ArtworkUrl.Resolve(RootBase, "/api/v2/artwork/k.webp?exp=1&sig=s")?.AbsoluteUri);
        Assert.Equal("https://nas.example/silo/api/v2/artwork/k?exp=1&sig=s",
            ArtworkUrl.Resolve(new Uri("https://nas.example/silo/"), "/api/v2/artwork/k?exp=1&sig=s")?.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//cdn.example/poster.png")]
    [InlineData("/\\cdn.example/poster.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Users/poster.png")]
    [InlineData("ftp://files.example/poster.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("poster.png")]
    public void Resolve_IsNull_WhenAbsentOrNotHttp(string? value)
    {
        Assert.Null(ArtworkUrl.Resolve(PrefixedBase, value));
    }
}
