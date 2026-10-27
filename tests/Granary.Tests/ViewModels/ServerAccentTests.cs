using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Theme;
using Granary.Core.Theme;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>The server's accent, as the web paints it over Cinema Dark (lib/accentColor.ts).</summary>
public sealed class ServerAccentTests
{
    private const string BrandingPath = "api/v2/theme/branding";

    private readonly LibraryHarness _harness = new();

    private ServerAccent Create() =>
        new(_harness.Session, new BrandingApi(_harness.Silo.Http), new ImmediateDispatcher(), _harness.Logs.For<ServerAccent>());

    [Theory]
    [InlineData("#3b82f6", "#3b82f6")]
    [InlineData(" #3B82F6 ", "#3b82f6")]
    [InlineData("#38f", "#3388ff")]
    public void AHexAccent_IsTheServers(string accent, string primary)
    {
        Assert.Equal(primary, AccentPalette.From(accent).Primary.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("red;} body{display:none")]
    [InlineData("#ef4444")]
    public void NoAccent_AnythingButHex_OrGranarysOwnRed_IsTheDefault(string? accent)
    {
        Assert.Same(AccentPalette.Default, AccentPalette.From(accent));
    }

    [Fact]
    public void ADarkAccent_TakesWhiteLabels_AndALightOneTheDarkLabel()
    {
        var blue = AccentPalette.From("#1d4ed8");
        var yellow = AccentPalette.From("#facc15");

        Assert.Equal((AccentPalette.White, AccentPalette.White), (blue.Label, blue.BadgeLabel));
        Assert.Equal((AccentPalette.DarkLabel, AccentPalette.DarkLabel), (yellow.Label, yellow.BadgeLabel));
    }

    [Fact]
    public void HoverIsLighter_AndPressDarker()
    {
        var blue = AccentPalette.From("#1d4ed8");

        Assert.True(blue.Hover.Luminance > blue.Primary.Luminance);
        Assert.True(blue.Pressed.Luminance < blue.Primary.Luminance);
    }

    [Fact]
    public async Task TheServersAccent_IsReadWithoutASignIn_AndPainted()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "server_name": "JoKeR", "accent_color": "#1d4ed8" }"""));
        var accent = Create();

        await accent.StartAsync();

        Assert.Equal("#1d4ed8", accent.Palette.Primary.ToString());
        Assert.Equal(SiloAuth.Anonymous, Assert.Single(_harness.Silo.SentTo(HttpMethod.Get, BrandingPath)).Auth);
    }

    [Fact]
    public async Task AFailedRead_IsGranarysOwnRed()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Problem(500, "internal_error"));
        var accent = Create();

        await accent.StartAsync();

        Assert.Same(AccentPalette.Default, accent.Palette);
    }

    [Fact]
    public async Task AnotherServer_IsReadAgain_AndTheSameOneNot()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#1d4ed8" }"""));
        var other = ScriptedSilo.Entry("srv2", new Uri("https://other.example/silo"));
        _harness.Silo.On(HttpMethod.Get, ScriptedSilo.Url(BrandingPath, other.BaseUri),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#16a34a" }"""));
        var accent = Create();
        await accent.StartAsync();

        _harness.Session.RaiseChanged();
        await accent.Reading;
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Get, BrandingPath));

        _harness.Session.ActiveServer = other;
        _harness.Session.RaiseChanged();
        await accent.Reading;
        Assert.Equal("#16a34a", accent.Palette.Primary.ToString());
    }

    [Fact]
    public async Task NoServer_IsGranarysOwnRed()
    {
        _harness.Silo.On(HttpMethod.Get, BrandingPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "accent_color": "#1d4ed8" }"""));
        var accent = Create();
        await accent.StartAsync();

        _harness.Session.ActiveServer = null;
        _harness.Session.RaiseChanged();
        await accent.Reading;

        Assert.Same(AccentPalette.Default, accent.Palette);
    }
}
