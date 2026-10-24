using Granary.Core.Artwork;

namespace Granary.Tests.Artwork;

public sealed class ArtworkCacheKeyTests
{
    [Fact]
    public void From_IgnoresTheSignature_SoARenewedUrlFindsTheSameImage()
    {
        string first = ArtworkCacheKey.From(new Uri("https://nas.example/silo/api/v2/artwork/movies/949/poster/w342.ab12.webp?exp=1&sig=a"));
        string renewed = ArtworkCacheKey.From(new Uri("https://nas.example/silo/api/v2/artwork/movies/949/poster/w342.ab12.webp?exp=2&sig=b"));

        Assert.Equal(first, renewed);
        Assert.Equal("https://nas.example/silo/api/v2/artwork/movies/949/poster/w342.ab12.webp", first);
    }

    [Fact]
    public void From_IgnoresObjectStorageSignatures()
    {
        string first = ArtworkCacheKey.From(new Uri("https://cdn.example/posters/heat.webp?X-Amz-Date=1&X-Amz-Signature=a%2Bb"));
        string renewed = ArtworkCacheKey.From(new Uri("https://cdn.example/posters/heat.webp?X-Amz-Date=2&X-Amz-Signature=c"));
        string token = ArtworkCacheKey.From(new Uri("https://cdn.example/posters/heat.webp?verify=1-abc"));

        Assert.Equal(first, renewed);
        Assert.Equal("https://cdn.example/posters/heat.webp", token);
    }

    [Fact]
    public void From_IgnoresTheOrderOfTheKeptParameters()
    {
        Assert.Equal(
            ArtworkCacheKey.From(new Uri("https://images.example/p.jpg?w=342&variant=card&sig=a")),
            ArtworkCacheKey.From(new Uri("https://images.example/p.jpg?sig=b&variant=card&w=342")));
    }

    [Fact]
    public void From_KeepsOtherQueryParameters_WhichMayNameADifferentImage()
    {
        string small = ArtworkCacheKey.From(new Uri("https://images.example/p.jpg?w=342&sig=a"));
        string large = ArtworkCacheKey.From(new Uri("https://images.example/p.jpg?w=780&sig=a"));

        Assert.NotEqual(small, large);
        Assert.Equal("https://images.example/p.jpg?w=342", small);
    }

    [Theory]
    [InlineData("https://nas.example/a/poster.webp", "https://nas.example/b/poster.webp")]
    [InlineData("https://nas.example/poster.webp", "https://other.example/poster.webp")]
    [InlineData("https://nas.example/poster.webp", "https://nas.example:8443/poster.webp")]
    [InlineData("https://nas.example/Poster.webp", "https://nas.example/poster.webp")]
    public void From_TellsDifferentImagesApart(string one, string other)
    {
        Assert.NotEqual(ArtworkCacheKey.From(new Uri(one)), ArtworkCacheKey.From(new Uri(other)));
    }

    [Fact]
    public void From_RefusesRelativeUrls()
    {
        Assert.Throws<ArgumentException>(() => ArtworkCacheKey.From(new Uri("/api/v2/artwork/k.webp", UriKind.Relative)));
    }
}
