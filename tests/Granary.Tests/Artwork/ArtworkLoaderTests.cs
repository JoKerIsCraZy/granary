using System.Net;
using System.Net.Http.Headers;
using Granary.Core.Artwork;
using Granary.Tests.Auth;
using Granary.Tests.Fakes;

namespace Granary.Tests.Artwork;

public sealed class ArtworkLoaderTests : IDisposable
{
    private static readonly Uri Poster = new("https://nas.example/api/v2/artwork/movies/949/poster/w342.ab12.webp?exp=1&sig=a");
    private static readonly Uri RenewedPoster = new("https://nas.example/api/v2/artwork/movies/949/poster/w342.ab12.webp?exp=2&sig=b");
    private static readonly Uri Still = new("https://nas.example/api/v2/artwork/episodes/1/w400.cd34.webp?exp=1&sig=c");
    private static readonly byte[] PosterBytes = [0x52, 0x49, 0x46, 0x46, 1, 2, 3];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly List<ArtworkLoader> _loaders = [];
    private readonly ArtworkServer _server = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var loader in _loaders)
        {
            loader.Dispose();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Get_DownloadsAnImageOnce_AndServesItFromMemoryAfterwards()
    {
        var loader = Create();

        var first = await loader.GetAsync(Poster, Token);
        var second = await loader.GetAsync(Poster, Token);

        Assert.Equal(PosterBytes, Read(first));
        Assert.Same(first, second);
        Assert.Equal(1, _server.Requests);
    }

    [Fact]
    public async Task Get_FindsTheImageAgain_WhenItsSignatureWasRenewed()
    {
        var loader = Create();

        await loader.GetAsync(Poster, Token);
        var renewed = await loader.GetAsync(RenewedPoster, Token);

        Assert.Equal(PosterBytes, Read(renewed));
        Assert.Equal(1, _server.Requests);
    }

    [Fact]
    public async Task Get_JoinsADownloadThatIsAlreadyRunning()
    {
        var gate = _server.Hold();
        var loader = Create();

        var first = loader.GetAsync(Poster, Token);
        var second = loader.GetAsync(Poster, Token);
        gate.SetResult();

        Assert.Same(await first, await second);
        Assert.Equal(1, _server.Requests);
    }

    [Fact]
    public async Task Get_AsksForHttp2WithoutCredentials()
    {
        await Create().GetAsync(Poster, Token);

        var request = Assert.Single(_server.Sent);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task ImmutableImages_AreKeptOnDiskForTheNextStart()
    {
        await Create().GetAsync(Poster, Token);

        var again = await Create().GetAsync(RenewedPoster, Token);

        Assert.Equal(PosterBytes, Read(again));
        Assert.Equal(1, _server.Requests);
    }

    [Fact]
    public async Task ImagesThatMayChange_StayOnlyInMemory()
    {
        _server.CacheControl = "private, no-cache";
        await Create().GetAsync(Poster, Token);

        await Create().GetAsync(Poster, Token);

        Assert.Equal(2, _server.Requests);
    }

    [Fact]
    public async Task AFailedImage_IsNotAskedForAgainForAMinute()
    {
        _server.Status = HttpStatusCode.NotFound;
        var loader = Create();

        Assert.Null(await loader.GetAsync(Poster, Token));
        Assert.Null(await loader.GetAsync(Poster, Token));
        Assert.Equal(1, _server.Requests);

        _time.Advance(TimeSpan.FromMinutes(1));
        _server.Status = HttpStatusCode.OK;

        Assert.NotNull(await loader.GetAsync(Poster, Token));
        Assert.Equal(2, _server.Requests);
        Assert.DoesNotContain("sig=", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswersThatAreNoImage_AreRefused()
    {
        _server.ContentType = "text/html";
        var loader = Create();

        Assert.Null(await loader.GetAsync(Poster, Token));
    }

    [Fact]
    public async Task ImagesLargerThanTheLimit_AreRefused()
    {
        var loader = Create(new ArtworkLoaderOptions { MaxImageBytes = PosterBytes.Length - 1 });

        Assert.Null(await loader.GetAsync(Poster, Token));
    }

    [Fact]
    public async Task ImagesLargerThanTheLimit_AreRefused_WhenTheServerSendsNoLength()
    {
        _server.SendsLength = false;
        var loader = Create(new ArtworkLoaderOptions { MaxImageBytes = PosterBytes.Length - 1 });

        Assert.Null(await loader.GetAsync(Poster, Token));
    }

    [Fact]
    public async Task ACancelledRequest_LeavesTheDownloadRunningForOthers()
    {
        var gate = _server.Hold();
        var loader = Create();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var abandoned = loader.GetAsync(Poster, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        gate.SetResult();

        Assert.NotNull(await loader.GetAsync(Poster, Token));
        Assert.Equal(1, _server.Requests);
    }

    [Fact]
    public async Task Prefetch_FetchesImagesBeforeTheyAreShown()
    {
        var loader = Create();

        loader.Prefetch([Poster, Still], Token);
        await AuthPipeline.Eventually(() => _server.Requests == 2);

        Assert.NotNull(await loader.GetAsync(Poster, Token));
        Assert.NotNull(await loader.GetAsync(Still, Token));
        Assert.Equal(2, _server.Requests);
    }

    [Fact]
    public async Task Prefetch_SkipsABatchThatWasCancelled()
    {
        var loader = Create(new ArtworkLoaderOptions { PrefetchWorkers = 1 });
        using var stale = new CancellationTokenSource();
        await stale.CancelAsync();

        loader.Prefetch([Poster], stale.Token);
        loader.Prefetch([Still], Token);
        await AuthPipeline.Eventually(() => _server.Requests == 1);

        Assert.Equal(Still.AbsolutePath, Assert.Single(_server.Sent).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Prefetch_SkipsImagesThatAreAlreadyStored()
    {
        await Create().GetAsync(Poster, Token);
        var loader = Create(new ArtworkLoaderOptions { PrefetchWorkers = 1 });

        loader.Prefetch([Poster, Still], Token);
        await AuthPipeline.Eventually(() => _server.Requests == 2);

        Assert.Equal(Still.AbsolutePath, _server.Sent[^1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Invalidate_ForgetsAnImageThatCouldNotBeShown()
    {
        var loader = Create();
        await loader.GetAsync(Poster, Token);

        loader.Invalidate(Poster);
        await loader.GetAsync(Poster, Token);

        Assert.Equal(2, _server.Requests);
    }

    private ArtworkLoader Create(ArtworkLoaderOptions? options = null)
    {
        var loader = new ArtworkLoader(
            new SingleClientFactory(_server),
            new ArtworkDiskCache(_directory, 1024 * 1024, _logs.For<ArtworkDiskCache>(), _time),
            _logs.For<ArtworkLoader>(),
            options,
            _time);
        _loaders.Add(loader);
        return loader;
    }

    private static byte[] Read(ArtworkBytes? artwork)
    {
        Assert.NotNull(artwork);
        using var stream = artwork.OpenRead();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(ArtworkLoader.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    /// <summary>Serves <see cref="PosterBytes"/> for every path, as the test configures it.</summary>
    private sealed class ArtworkServer : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<HttpRequestMessage> _sent = [];
        private TaskCompletionSource? _hold;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string ContentType { get; set; } = "image/webp";

        public string CacheControl { get; set; } = "private, max-age=3600, immutable";

        public bool SendsLength { get; set; } = true;

        public int Requests
        {
            get
            {
                lock (_gate)
                {
                    return _sent.Count;
                }
            }
        }

        public IReadOnlyList<HttpRequestMessage> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        /// <summary>Holds every answer until the test completes the returned source.</summary>
        public TaskCompletionSource Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _sent.Add(request);
            }
            if (_hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }
            HttpContent content = SendsLength ? new ByteArrayContent(PosterBytes) : new StreamContent(new UnknownLengthStream(PosterBytes));
            content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
            var response = new HttpResponseMessage(Status) { Content = content, Version = request.Version };
            response.Headers.CacheControl = CacheControlHeaderValue.Parse(CacheControl);
            return response;
        }
    }

    /// <summary>A stream that cannot tell its length, like a chunked answer.</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
