using System.Net;
using System.Net.Http.Headers;
using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Artwork;
using Granary.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Granary.Tests.Artwork;

/// <summary>The artwork loader as the app registers it, with only the network and the cache folder replaced.</summary>
public sealed class ArtworkCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheArtworkClient_SendsNoCredentialsAndNoDeviceHeaders()
    {
        // Artwork URLs are presigned and public: whatever the pipeline holds, a request carries neither.
        var network = new FakeHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INavigationService, RecordingNavigation>();
        services.AddGranaryCore();
        services.AddHttpClient(ArtworkLoader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => network);
        services.AddSingleton(sp => new ArtworkDiskCache(_directory, 1024 * 1024, sp.GetRequiredService<ILogger<ArtworkDiskCache>>()));
        using var provider = services.BuildServiceProvider();

        var artwork = await provider.GetRequiredService<ArtworkLoader>()
            .GetAsync(new Uri("https://nas.example/api/v2/artwork/movies/1/poster/w342.ab.webp?exp=1&sig=s"), TestContext.Current.CancellationToken);

        Assert.NotNull(artwork);
        var request = Assert.Single(network.Requests);
        Assert.Null(request.Headers.Authorization);
        Assert.DoesNotContain(request.Headers, h => h.Key.StartsWith("X-Silo-", StringComparison.OrdinalIgnoreCase) || h.Key.StartsWith("X-Profile-", StringComparison.OrdinalIgnoreCase));
    }
}
