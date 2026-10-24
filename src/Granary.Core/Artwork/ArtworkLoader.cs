using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Artwork;

/// <summary>
/// Loads artwork for display: from memory, then disk, then the network. Artwork URLs are presigned and public
/// (docs/api-contract.md, 7.10), so requests carry no credentials. Requests for one image share one download,
/// and a failed URL is not asked for again for <see cref="ArtworkLoaderOptions.FailureHold"/>. The work runs on
/// the thread pool, never on the thread of the caller (the UI thread).
/// </summary>
/// <remarks>
/// An image the server marks <c>immutable</c> is kept under its <see cref="ArtworkCacheKey"/>, in memory and on
/// disk across starts, so a renewed signature finds it. Any other image can be replaced in place: it stays in
/// memory only, under its full URL, which the server renews every 15 minutes.
/// <para>
/// Images on screen and prefetched images download side by side, each with their own limit, so a long
/// prefetch queue never delays what the user is looking at.
/// </para>
/// </remarks>
public sealed class ArtworkLoader : IArtworkPrefetcher, IDisposable
{
    public const string HttpClientName = "Granary.Artwork";

    private const int ReadChunkBytes = 64 * 1024;

    // Failed URLs are forgotten once their hold is over, when this many have piled up.
    private const int FailuresBeforePruning = 256;

    private readonly IHttpClientFactory _http;
    private readonly ArtworkDiskCache _disk;
    private readonly ILogger<ArtworkLoader> _logger;
    private readonly ArtworkLoaderOptions _options;
    private readonly TimeProvider _time;
    private readonly ArtworkMemoryCache _memory;
    private readonly SemaphoreSlim _downloads;
    private readonly Channel<PrefetchRequest> _prefetches = Channel.CreateUnbounded<PrefetchRequest>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<ArtworkBytes?>> _loads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.Ordinal);
    private bool _prefetching;

    public ArtworkLoader(
        IHttpClientFactory http,
        ArtworkDiskCache disk,
        ILogger<ArtworkLoader> logger,
        ArtworkLoaderOptions? options = null,
        TimeProvider? time = null)
    {
        _http = http;
        _disk = disk;
        _logger = logger;
        _options = options ?? new ArtworkLoaderOptions();
        _time = time ?? TimeProvider.System;
        _memory = new ArtworkMemoryCache(_options.MemoryCapacityBytes);
        _downloads = new SemaphoreSlim(_options.MaxConcurrentDownloads);
    }

    /// <summary>The image at <paramref name="url"/>, or null when it cannot be had (the reason is logged).</summary>
    /// <remarks>Cancelling ends only this wait: the download goes on for other requests and the cache.</remarks>
    public Task<ArtworkBytes?> GetAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        string key = ArtworkCacheKey.From(url);
        return _memory.TryGet(key, out var cached) || _memory.TryGet(url.AbsoluteUri, out cached)
            ? Task.FromResult<ArtworkBytes?>(cached)
            : StartOrJoin(url, key, onScreen: true).WaitAsync(cancellationToken);
    }

    public void Prefetch(IEnumerable<Uri> urls, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urls);
        StartPrefetching();
        foreach (var url in urls)
        {
            if (url is { IsAbsoluteUri: true })
            {
                _prefetches.Writer.TryWrite(new PrefetchRequest(url, cancellationToken));
            }
        }
    }

    /// <summary>Forgets a stored image that turned out to be unusable (it would not decode), so the next request downloads it again.</summary>
    public void Invalidate(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string key = ArtworkCacheKey.From(url);
        _memory.Remove(key);
        _memory.Remove(url.AbsoluteUri);
        _disk.Remove(key);
    }

    public void Dispose()
    {
        _prefetches.Writer.TryComplete();
        _lifetime.Cancel();
    }

    private Task<ArtworkBytes?> StartOrJoin(Uri url, string key, bool onScreen)
    {
        TaskCompletionSource<ArtworkBytes?> load;
        lock (_gate)
        {
            if (_loads.TryGetValue(key, out var running))
            {
                return running;
            }
            // A load that finished since the caller looked keeps its image in memory.
            if (_memory.TryGet(key, out var cached) || _memory.TryGet(url.AbsoluteUri, out cached))
            {
                return Task.FromResult<ArtworkBytes?>(cached);
            }
            // Held per URL: an expired signature fails, and the renewed URL for the same image must not wait.
            if (_failedUntil.TryGetValue(url.AbsoluteUri, out var until))
            {
                if (_time.GetUtcNow() < until)
                {
                    return Task.FromResult<ArtworkBytes?>(null);
                }
                _failedUntil.Remove(url.AbsoluteUri);
            }
            load = new TaskCompletionSource<ArtworkBytes?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loads[key] = load.Task;
        }
        _ = Task.Run(() => RunAsync(url, key, onScreen, load));
        return load.Task;
    }

    // Never throws: every waiter gets the image or null.
    private async Task RunAsync(Uri url, string key, bool onScreen, TaskCompletionSource<ArtworkBytes?> load)
    {
        ArtworkBytes? result = null;
        try
        {
            result = await LoadAsync(url, key, onScreen, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The path only: the query holds the signature.
            _logger.LogWarning("Artwork {Path} could not be loaded: {Failure}", url.AbsolutePath, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _loads.Remove(key);
                if (result is null && !_lifetime.IsCancellationRequested)
                {
                    HoldFailed(url.AbsoluteUri);
                }
            }
            load.SetResult(result);
        }
    }

    // Called under the lock.
    private void HoldFailed(string url)
    {
        var now = _time.GetUtcNow();
        if (_failedUntil.Count >= FailuresBeforePruning)
        {
            foreach (var (expired, _) in _failedUntil.Where(f => f.Value <= now).ToList())
            {
                _failedUntil.Remove(expired);
            }
        }
        _failedUntil[url] = now + _options.FailureHold;
    }

    private async Task<ArtworkBytes?> LoadAsync(Uri url, string key, bool onScreen, CancellationToken cancellationToken)
    {
        // Only immutable images are on disk.
        if (await _disk.ReadAsync(key, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            return Remember(key, stored);
        }
        Download? download;
        if (onScreen)
        {
            await _downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            download = await DownloadWithinTimeoutAsync(url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (onScreen)
            {
                _downloads.Release();
            }
        }
        if (download is null)
        {
            return null;
        }
        if (!download.IsImmutable)
        {
            return Remember(url.AbsoluteUri, download.Data);
        }
        await _disk.WriteAsync(key, download.Data, cancellationToken).ConfigureAwait(false);
        return Remember(key, download.Data);
    }

    private ArtworkBytes Remember(string identity, byte[] data)
    {
        var bytes = new ArtworkBytes(data);
        _memory.Add(identity, bytes);
        return bytes;
    }

    private async Task<Download?> DownloadWithinTimeoutAsync(Uri url, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.DownloadTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await DownloadAsync(url, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Artwork {Path} did not arrive within {Timeout}", url.AbsolutePath, _options.DownloadTimeout);
            return null;
        }
    }

    private async Task<Download?> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        using var client = _http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        string? refusal = response switch
        {
            { IsSuccessStatusCode: false } => $"status {(int)response.StatusCode}",
            _ when !IsImageType(response.Content.Headers.ContentType?.MediaType) => "the answer is not an image",
            _ when response.Content.Headers.ContentLength > _options.MaxImageBytes => "the image is too large",
            _ => null,
        };
        var data = refusal is null ? await ReadAtMostAsync(response.Content, _options.MaxImageBytes, cancellationToken).ConfigureAwait(false) : null;
        if (data is null)
        {
            _logger.LogDebug("Artwork {Path} was refused: {Reason}", url.AbsolutePath, refusal ?? "the image is too large");
            return null;
        }
        bool immutable = response.Headers.CacheControl?.Extensions.Any(e => string.Equals(e.Name, "immutable", StringComparison.OrdinalIgnoreCase)) == true;
        return new Download(data, immutable);
    }

    // Object storage may label images generically; anything else (an HTML error page, JSON) is no image.
    private static bool IsImageType(string? mediaType) =>
        mediaType is null
        || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase);

    // Null when the body is longer than maxBytes, which a missing or wrong Content-Length does not reveal.
    // A stated length sizes the buffer up front, so a correct one costs no growing and no copy.
    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(content.Headers.ContentLength is { } length and > 0 && length <= maxBytes ? (int)length : 0);
        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }

    private void StartPrefetching()
    {
        lock (_gate)
        {
            if (_prefetching)
            {
                return;
            }
            _prefetching = true;
        }
        for (int i = 0; i < _options.PrefetchWorkers; i++)
        {
            _ = Task.Run(PrefetchAsync);
        }
    }

    private async Task PrefetchAsync()
    {
        try
        {
            await foreach (var request in _prefetches.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                string key = ArtworkCacheKey.From(request.Url);
                if (!_memory.Contains(key) && !_memory.Contains(request.Url.AbsoluteUri) && !_disk.Contains(key))
                {
                    await StartOrJoin(request.Url, key, onScreen: false).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private readonly record struct PrefetchRequest(Uri Url, CancellationToken CancellationToken);

    private sealed record Download(byte[] Data, bool IsImmutable);
}
