using Granary.Core.Trailers;

namespace Granary.Tests.Trailers;

/// <summary>Where the trailer's WebView may go, what its frames may load, and which links open in the browser.</summary>
public sealed class TrailerWebPolicyTests
{
    // Lookalikes: a Cyrillic "о" in place of the Latin one, written raw and as the browser hands it on (punycode).
    private const string CyrillicHost = "https://www.y\u043Eutube.com";
    private const string PunycodeHost = "https://www.xn--yutube-7nf.com";

    [Fact]
    public void TheWebView_GoesToGranarysPage() => Assert.True(TrailerWebPolicy.AllowsNavigation("https://trailer.granary.example/player"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // The embed itself, top level, has no Referer: YouTube answers it with error 153.
    [InlineData("https://www.youtube-nocookie.com/embed/xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com/watch?v=xEQP4VVuyrY")]
    [InlineData("http://trailer.granary.example/player")]
    [InlineData("https://trailer.granary.example/")]
    [InlineData("https://trailer.granary.example/other")]
    [InlineData("https://trailer.granary.example/player?v=xEQP4VVuyrY")]
    [InlineData("https://trailer.granary.example:8443/player")]
    [InlineData("https://user@trailer.granary.example/player")]
    [InlineData("https://trailer.granary.example.evil.example/player")]
    [InlineData("https://evil.example/trailer.granary.example/player")]
    [InlineData("https://evil.example#https://trailer.granary.example/player")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<p>hello</p>")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void TheWebView_GoesNowhereElse(string? url) => Assert.False(TrailerWebPolicy.AllowsNavigation(url));

    [Theory]
    [InlineData("https://www.youtube-nocookie.com/embed/xEQP4VVuyrY?autoplay=1&rel=0&playsinline=1&enablejsapi=1&origin=https%3A%2F%2Ftrailer.granary.example")]
    [InlineData("https://www.youtube-nocookie.com/embed/xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com/embed/xEQP4VVuyrY?autoplay=1")]
    public void AFrame_LoadsYouTubesEmbeddedPlayer(string url) => Assert.True(TrailerWebPolicy.AllowsFrame(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://www.youtube-nocookie.com/embed/xEQP4VVuyrY")]
    [InlineData("http://www.youtube.com/embed/xEQP4VVuyrY")]
    // Other paths on YouTube.
    [InlineData("https://www.youtube.com/watch?v=xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com/redirect?q=https%3A%2F%2Fevil.example")]
    [InlineData("https://www.youtube.com/embed/")]
    [InlineData("https://www.youtube.com/embed")]
    [InlineData("https://www.youtube.com/embedded/xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com/embed/xEQP4VVuyrY/extra")]
    [InlineData("https://www.youtube.com/embed/xEQP4VVuyrY/x?autoplay=1")]
    [InlineData("https://www.youtube.com/embed/xEQP4VVuyrY%3Fx")]
    [InlineData("https://www.youtube.com/embed/xEQP4VVuyrY;x")]
    [InlineData("https://www.youtube.com/embed/../redirect?q=https://evil.example")]
    [InlineData("https://www.youtube.com/embed/%2e%2e/redirect")]
    [InlineData("https://www.youtube.com/embed/xEQP4VVuyrY%2F..%2Fredirect")]
    [InlineData("https://www.youtube-nocookie.com/")]
    // Other YouTube hosts, and hosts that only look like them.
    [InlineData("https://youtube.com/embed/xEQP4VVuyrY")]
    [InlineData("https://m.youtube.com/embed/xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com./embed/xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com.evil.example/embed/xEQP4VVuyrY")]
    [InlineData("https://evil.example/www.youtube.com/embed/xEQP4VVuyrY")]
    [InlineData(CyrillicHost + "/embed/xEQP4VVuyrY")]
    [InlineData(PunycodeHost + "/embed/xEQP4VVuyrY")]
    // Userinfo, ports and backslashes.
    [InlineData("https://www.youtube.com@evil.example/embed/xEQP4VVuyrY")]
    [InlineData("https://evil.example@www.youtube.com/embed/xEQP4VVuyrY")]
    [InlineData("https://user:password@www.youtube-nocookie.com/embed/xEQP4VVuyrY")]
    [InlineData("https://www.youtube-nocookie.com:444/embed/xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com\\@evil.example/embed/xEQP4VVuyrY")]
    [InlineData("https://www.youtube-nocookie.com/embed/xEQP4VVuyrY\n")]
    // Not the web at all.
    [InlineData("javascript:alert(document.domain)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("about:blank")]
    [InlineData("about:srcdoc")]
    [InlineData("blob:https://www.youtube-nocookie.com/0b4a1f0c-1b0e-4a5e-9e43-2d3a1f7c9b11")]
    [InlineData("https://trailer.granary.example/player")]
    public void AFrame_LoadsNothingElse(string? url) => Assert.False(TrailerWebPolicy.AllowsFrame(url));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=xEQP4VVuyrY")]
    [InlineData("https://youtube.com/channel/UC1234567890")]
    [InlineData("https://m.youtube.com/watch?v=xEQP4VVuyrY")]
    [InlineData("https://youtu.be/xEQP4VVuyrY")]
    public void YouTubesLinks_OpenInTheBrowser(string url) => Assert.Equal(new Uri(url), TrailerWebPolicy.BrowserTarget(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://www.youtube.com/watch?v=xEQP4VVuyrY")]
    [InlineData("https://www.youtube-nocookie.com/embed/xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=xEQP4VVuyrY")]
    [InlineData("https://youtu.be.evil.example/xEQP4VVuyrY")]
    [InlineData("https://evil.example/?next=https://www.youtube.com/")]
    [InlineData("https://www.youtube.com@evil.example/")]
    [InlineData("https://user:password@www.youtube.com/watch?v=xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com:8080/watch?v=xEQP4VVuyrY")]
    [InlineData("https://www.youtube.com./watch?v=xEQP4VVuyrY")]
    [InlineData(CyrillicHost + "/watch?v=xEQP4VVuyrY")]
    [InlineData(PunycodeHost + "/watch?v=xEQP4VVuyrY")]
    [InlineData("https://accounts.google.com/ServiceLogin")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<p>hello</p>")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("vnd.youtube:xEQP4VVuyrY")]
    [InlineData("ms-settings:privacy")]
    public void EveryOtherLink_IsDropped(string? url) => Assert.Null(TrailerWebPolicy.BrowserTarget(url));

    [Theory]
    [InlineData("https://trailer.granary.example/player", true)]
    [InlineData("https://trailer.granary.example/", true)]
    [InlineData("http://trailer.granary.example/player", false)]
    [InlineData("https://trailer.granary.example:444/player", false)]
    [InlineData("https://trailer.granary.example.evil.example/player", false)]
    [InlineData("https://www.youtube-nocookie.com/embed/xEQP4VVuyrY", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyGranarysOwnPage_SpeaksToGranary(string? source, bool own) => Assert.Equal(own, TrailerWebPolicy.IsOwnOrigin(source));

    private const string Page = "https://trailer.granary.example/player";
    private const string Embed = "https://www.youtube-nocookie.com/embed/xEQP4VVuyrY?autoplay=1&enablejsapi=1";

    [Theory]
    [InlineData("{\"kind\":\"ready\"}", TrailerPageEvent.Ready)]
    [InlineData("{\"kind\":\"error\",\"code\":150}", TrailerPageEvent.Error)]
    [InlineData("{\"kind\":\"close\"}", TrailerPageEvent.Close)]
    public void ThePage_SaysAllItsMessages(string json, TrailerPageEvent kind) =>
        Assert.Equal(kind, TrailerWebPolicy.FromPage(Page, json)?.Event);

    [Theory]
    [InlineData(Embed, "{\"kind\":\"ready\"}")]
    [InlineData("https://evil.example/", "{\"kind\":\"ready\"}")]
    [InlineData(Page, "{\"kind\":\"what\"}")]
    [InlineData(Page, null)]
    public void NothingElse_SpeaksForThePage(string source, string? json) => Assert.Null(TrailerWebPolicy.FromPage(source, json));

    [Fact]
    public void YouTubesPlayer_CanOnlyAskToClose() =>
        // Granary's key listener runs in the player's frame too (Esc, Back): closing is all a frame may ask, and all a
        // frame that is not the player may not.
        Assert.Equal(TrailerPageEvent.Close, TrailerWebPolicy.FromEmbed(Embed, "{\"kind\":\"close\"}")?.Event);

    [Theory]
    [InlineData(Embed, "{\"kind\":\"ready\"}")]
    [InlineData(Embed, "{\"kind\":\"playing\"}")]
    [InlineData(Embed, "{\"kind\":\"error\",\"code\":150}")]
    [InlineData(Embed, "{\"close\":true}")]
    [InlineData("https://googleads.g.doubleclick.net/pagead/ads", "{\"kind\":\"close\"}")]
    [InlineData("about:blank", "{\"kind\":\"close\"}")]
    [InlineData(Page, "{\"kind\":\"close\"}")]
    [InlineData(null, "{\"kind\":\"close\"}")]
    public void AFrame_SaysNothingElse(string? source, string json) => Assert.Null(TrailerWebPolicy.FromEmbed(source, json));
}
