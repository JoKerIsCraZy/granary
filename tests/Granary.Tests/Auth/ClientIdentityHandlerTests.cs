using System.Net;
using Granary.Core.Api;
using Granary.Core.Http;
using Granary.Core.Texts;
using Granary.Tests.Fakes;
using Granary.Tests.Texts;

namespace Granary.Tests.Auth;

/// <summary>
/// Every request to a Silo server says the language on screen in <c>Accept-Language</c>, as Silo's Android and Apple
/// apps do (silo-server docs/architecture/i18n.md, "Accept-Language"); requests to other origins say nothing.
/// </summary>
public sealed class ClientIdentityHandlerTests
{
    private readonly FakeHttpMessageHandler _server = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");

    [Fact]
    public async Task ASiloRequest_SaysTheLanguageOnScreen()
    {
        await SendAsync(toServer: true);

        Assert.Equal(["en"], Languages(Sent()));
    }

    [Fact]
    public async Task TheLanguage_IsTheOneApplied()
    {
        using (AppText.Use(TextFixtures.Language("de")))
        {
            await SendAsync(toServer: true);
        }

        Assert.Equal(["de"], Languages(Sent()));
    }

    [Fact]
    public async Task TheLanguage_ReplacesOneSetBefore()
    {
        await SendAsync(toServer: true, language: "fr");

        Assert.Equal(["en"], Languages(Sent()));
    }

    [Fact]
    public async Task AnotherOrigin_HearsNoLanguage()
    {
        await SendAsync(toServer: false);

        Assert.Empty(Languages(Sent()));
    }

    private HttpRequestMessage Sent() => Assert.Single(_server.Requests);

    private static string[] Languages(HttpRequestMessage request) =>
        request.Headers.TryGetValues("Accept-Language", out var values) ? [.. values] : [];

    private async Task SendAsync(bool toServer, string? language = null)
    {
        using var invoker = new HttpMessageInvoker(new ClientIdentityHandler(AuthPipeline.Identity) { InnerHandler = _server });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://silo.test/api/v2/system/info");
        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }
        if (toServer)
        {
            request.SetServer(new Uri("https://silo.test"));
        }

        using var _ = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
