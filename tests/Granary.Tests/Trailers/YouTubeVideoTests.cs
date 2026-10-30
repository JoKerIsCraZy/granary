using Granary.Core.Trailers;

namespace Granary.Tests.Trailers;

public sealed class YouTubeVideoTests
{
    [Fact]
    public void TheEmbed_IsThePrivacyEnhancedOne_PlayingAtOnce_AndAnsweringOnlyGranarysPage()
    {
        var embed = YouTubeVideo.EmbedUrl("xEQP4VVuyrY", TrailerPage.Origin);

        Assert.Equal(
            "https://www.youtube-nocookie.com/embed/xEQP4VVuyrY?autoplay=1&rel=0&playsinline=1&enablejsapi=1&origin=https%3A%2F%2Ftrailer.granary.example",
            embed.AbsoluteUri);
    }

    [Fact]
    public void TheWatchPageAndThumbnail_AreYouTubesOwn()
    {
        Assert.Equal(new Uri("https://www.youtube.com/watch?v=xEQP4VVuyrY"), YouTubeVideo.WatchUrl("xEQP4VVuyrY"));
        Assert.Equal(new Uri("https://i.ytimg.com/vi/xEQP4VVuyrY/hqdefault.jpg"), YouTubeVideo.ThumbnailUrl("xEQP4VVuyrY"));
    }

    [Theory]
    [InlineData("xEQP4VVuyrY")]
    [InlineData("a-b_c")]
    public void KeysOfLettersDigitsDashAndUnderscore_AreKeys(string key) => Assert.True(YouTubeVideo.IsKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x&list=evil")]
    [InlineData("../watch")]
    [InlineData("https://evil.example/")]
    [InlineData("abc def")]
    [InlineData("\"><script>alert(1)</script>")]
    [InlineData("vidéo")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void AnythingElse_IsNoKey_AndBuildsNoAddress(string? key)
    {
        Assert.False(YouTubeVideo.IsKey(key));
        Assert.ThrowsAny<ArgumentException>(() => YouTubeVideo.EmbedUrl(key!, TrailerPage.Origin));
        Assert.ThrowsAny<ArgumentException>(() => YouTubeVideo.WatchUrl(key!));
        Assert.ThrowsAny<ArgumentException>(() => TrailerPage.Html(key!));
    }
}
