using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Granary.Core.Trailers;

namespace Granary.Tests.Trailers;

public sealed partial class TrailerPageTests
{
    private const string Key = "xEQP4VVuyrY";

    [Fact]
    public void ThePage_IsGranarysOwn_OnAnHttpsHostNothingElseAnswers()
    {
        Assert.Equal(new Uri("https://trailer.granary.example/player"), TrailerPage.Url);
        Assert.Equal(new Uri("https://trailer.granary.example"), TrailerPage.Origin);
        Assert.Equal("https://trailer.granary.example/*", TrailerPage.Filter);
    }

    [Fact]
    public void ThePage_FramesTheEmbed_WithAReferrer_AndWhatThePlayerMayUse()
    {
        string html = TrailerPage.Html(Key);

        var frame = Assert.Single(Frames().Matches(html));
        string tag = frame.Value;
        Assert.Contains(
            "src=\"https://www.youtube-nocookie.com/embed/xEQP4VVuyrY?autoplay=1&amp;rel=0&amp;playsinline=1&amp;enablejsapi=1&amp;origin=https%3A%2F%2Ftrailer.granary.example\"",
            tag, StringComparison.Ordinal);
        Assert.Contains("referrerpolicy=\"strict-origin-when-cross-origin\"", tag, StringComparison.Ordinal);
        Assert.Contains("allow=\"autoplay; encrypted-media; fullscreen; picture-in-picture\"", tag, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"referrer\" content=\"strict-origin-when-cross-origin\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_RunsOnlyItsOwnScript_NoneOfYouTubes()
    {
        string html = TrailerPage.Html(Key);

        // The IFrame Player API is spoken over postMessage by the page itself: YouTube's own API script would run in the one
        // document that can message Granary.
        Assert.DoesNotContain("<script src", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe_api", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.Single(Scripts().Matches(html));
    }

    [Fact]
    public void ThePagesPolicy_AllowsExactlyItsInlineScriptAndStyle_AndFramesOnlyTheEmbed()
    {
        string html = TrailerPage.Html(Key);
        string policy = Header(TrailerPage.Headers, "Content-Security-Policy");

        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-src https://www.youtube-nocookie.com https://www.youtube.com;", policy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", policy, StringComparison.Ordinal);
        Assert.Contains($"script-src 'sha256-{Hash(Scripts().Match(html).Groups[1].Value)}'", policy, StringComparison.Ordinal);
        Assert.Contains($"style-src 'sha256-{Hash(Styles().Match(html).Groups[1].Value)}'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_IsHtml_NeverCachedNorSniffed()
    {
        Assert.Equal("text/html; charset=utf-8", Header(TrailerPage.Headers, "Content-Type"));
        Assert.Equal("no-store", Header(TrailerPage.Headers, "Cache-Control"));
        Assert.Equal("nosniff", Header(TrailerPage.Headers, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(TrailerPage.Headers, "Referrer-Policy"));
    }

    [Fact]
    public void ThePage_DiffersOnlyByTheKey()
    {
        // Nothing of the server, the profile or the title's metadata goes into the page: only the validated key.
        Assert.Equal(TrailerPage.Html(Key).Replace(Key, "aaaaaaaaaaa", StringComparison.Ordinal), TrailerPage.Html("aaaaaaaaaaa"));
    }

    private static string Header(string headers, string name) =>
        headers.Split("\r\n").Select(line => line.Split(": ", 2)).Single(parts => parts[0] == name)[1];

    private static string Hash(string content) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [GeneratedRegex("<iframe [^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Frames();

    [GeneratedRegex("<script>(.*?)</script>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Scripts();

    [GeneratedRegex("<style>(.*?)</style>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Styles();
}
