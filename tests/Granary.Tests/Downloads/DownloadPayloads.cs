using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Granary.Tests.Downloads;

/// <summary>The server's downloads answers, shaped as its client guide shows them (downloads-api.md, sections 3 to 6).</summary>
internal static class DownloadPayloads
{
    // Each preset's video cap and tallest output, as the guide's capability example lists them.
    private static readonly Dictionary<string, (int Kbps, int Height)> Presets = new(StringComparer.Ordinal)
    {
        ["20mbps"] = (20000, 2160),
        ["10mbps"] = (10000, 1080),
        ["5mbps"] = (5000, 1080),
        ["2mbps"] = (2000, 720),
        ["1mbps"] = (1000, 480),
    };

    /// <summary><c>GET /capabilities/downloads</c>; without presets, <c>original</c> alone (none when downloads are off).</summary>
    public static string Capability(bool enabled = true, bool allowed = true, params string[] presets)
    {
        string[] offered = presets.Length > 0 ? presets : enabled && allowed ? ["original"] : [];
        string names = string.Join(", ", offered.Select(p => $"\"{p}\""));
        string options = string.Join(", ", offered.Select(p => Presets.TryGetValue(p, out var preset)
            ? $$"""{ "preset": "{{p}}", "bitrate_kbps": {{preset.Kbps}}, "max_height": {{preset.Height}} }"""
            : $$"""{ "preset": "{{p}}" }"""));
        return $$"""
            { "revision": "cap-1", "state": "available", "enabled": {{Bool(enabled)}}, "allowed": {{Bool(allowed)}},
              "download_allowed": {{Bool(allowed)}}, "quality_presets": [{{names}}], "quality_options": [{{options}}],
              "transcode_enabled": true, "transcode_user_allowed": true, "season_download": true, "proxy_delivery": false,
              "ordered_status": true, "file_delivery": true, "bounded_creation": true, "bounded_manifests": true,
              "preparation_progress": true, "prepare_again": true, "revoked_removes_local": true }
            """;
    }

    /// <summary>
    /// One registry entry: a film's (<c>mv_1</c>), or with <paramref name="episodeId"/> an episode of <c>sr_1</c>. A preparing one
    /// with <paramref name="progress"/> lists its encode's progress, as <c>GET /downloads</c> does.
    /// </summary>
    public static string Entry(string id, string status, long revision = 1, long size = 3000, string? episodeId = null, double? progress = null)
    {
        string content = episodeId is null ? "\"content_id\": \"mv_1\"" : $"\"content_id\": \"sr_1\", \"episode_id\": \"{episodeId}\"";
        string preparation = progress is { } fraction
            ? $$""", "preparation": { "state": "running", "progress": {{fraction.ToString(CultureInfo.InvariantCulture)}}, "remaining_seconds": 120 }"""
            : "";
        return $$"""
            { "id": "{{id}}", {{content}}, "media_file_id": "4567", "device_id": "device-1",
              "file_size": {{size.ToString(CultureInfo.InvariantCulture)}}, "bytes_sent": 0, "kind": "queued", "status": "{{status}}",
              "quality": "original", "effective_quality": "original", "delivery_format": "original", "target_bitrate_kbps": 0,
              "revision": {{revision.ToString(CultureInfo.InvariantCulture)}}, "created_at": "2026-10-01T11:00:00Z"{{preparation}} }
            """;
    }

    /// <summary>The capability with the two flags the queue reads set as given.</summary>
    public static string CapabilityWith(bool proxyDelivery = false, bool revokedRemovesLocal = true) =>
        Capability()
            .Replace("\"proxy_delivery\": false", $"\"proxy_delivery\": {Bool(proxyDelivery)}", StringComparison.Ordinal)
            .Replace("\"revoked_removes_local\": true", $"\"revoked_removes_local\": {Bool(revokedRemovesLocal)}", StringComparison.Ordinal);

    /// <summary>An artwork or subtitle answer: its bytes as <paramref name="mediaType"/>.</summary>
    public static Func<Task<HttpResponseMessage>> Asset(byte[] body, string mediaType) => () =>
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    };

    /// <summary>A page of entries, as <c>GET /downloads</c> and <c>POST /downloads</c> answer.</summary>
    public static string Page(string? nextCursor, params string[] entries)
    {
        string page = nextCursor is null ? """{ "has_more": false }""" : $$"""{ "has_more": true, "next_cursor": "{{nextCursor}}" }""";
        return $$"""{ "items": [{{string.Join(", ", entries)}}], "skipped": [], "page": {{page}} }""";
    }

    /// <summary>
    /// The guide's manifest (section 6) for episode 5 of "Example Series", season 1: two audio tracks, the second chosen; a
    /// chapter, intro and credits markers; one external SRT; poster, backdrop and series poster.
    /// </summary>
    public static string Manifest(string id, string container = "mkv", long expectedBytes = 3000, string subtitleRevision = "r1")
    {
        string bytes = expectedBytes.ToString(CultureInfo.InvariantCulture);
        return $$"""
            {
              "download_id": "{{id}}", "content_id": "sr_1", "episode_id": "ep_5", "type": "episode", "revision": 1,
              "quality": "original", "effective_quality": "original", "delivery_format": "original", "target_bitrate_kbps": 0,
              "media_file_id": "4567", "file_size": {{bytes}},
              "title": "Chapter Five", "year": 2024, "overview": "Elena finds the map.", "runtime": 7200,
              "series_id": "sr_1", "series_title": "Example Series", "season_number": 1, "episode_number": 5,
              "poster_thumbhash": "iQ", "backdrop_thumbhash": "iA", "series_poster_thumbhash": "iS",
              "artwork_urls": {
                "poster": "/api/v2/downloads/{{id}}/artwork/poster",
                "backdrop": "/api/v2/downloads/{{id}}/artwork/backdrop",
                "series_poster": "/api/v2/downloads/{{id}}/artwork/series_poster"
              },
              "container": "{{container}}", "codec_video": "h264", "codec_audio": "aac", "resolution": "1080p", "hdr": false,
              "duration_seconds": 7200, "selected_audio_track_index": 1,
              "audio_tracks": [
                { "index": 0, "language": "en", "codec": "aac", "layout": "5.1", "channels": 6, "default": true },
                { "index": 1, "title": "Commentary", "language": "en", "codec": "aac", "layout": "stereo", "channels": 2, "default": false }
              ],
              "chapters": [ { "index": 0, "title": "Cold Open", "start_seconds": 0, "end_seconds": 142.5, "thumbnail_thumbhash": "iC" } ],
              "intro": { "start": 60.0, "end": 90.0 },
              "credits": { "start": 7100.0, "end": 7200.0 },
              "marker_segments": [
                { "kind": "intro", "start_seconds": 60.0, "end_seconds": 90.0 },
                { "kind": "credits", "start_seconds": 7100.0, "end_seconds": 7200.0 }
              ],
              "subtitles": [
                { "language": "en", "format": "srt", "forced": false, "hearing_impaired": false, "external": true,
                  "fetch_url": "/api/v2/downloads/{{id}}/subtitles/external:0", "file_size": 41234, "revision": "{{subtitleRevision}}" }
              ],
              "stable_identity": { "stable_type": "episode", "provider_ids": { "tmdb": "12345" } },
              "integrity": { "expected_bytes": {{bytes}}, "media_file_hash": "sha256-example", "metadata_etag": "m1" },
              "manifest_version": 3, "generated_at": "2026-10-01T11:05:00.000Z"
            }
            """;
    }

    /// <summary>
    /// Media bytes: a 200, or with <paramref name="rangeFrom"/> a 206 whose <c>Content-Range</c> starts there and covers the
    /// body, out of <paramref name="total"/> (<c>*</c> when null). <paramref name="lastModified"/> is its <c>Last-Modified</c>.
    /// </summary>
    public static Func<Task<HttpResponseMessage>> Bytes(HttpStatusCode status, byte[] body, string? etag, long? rangeFrom = null, long? total = null,
        DateTimeOffset? lastModified = null) => () =>
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.LastModified = lastModified;
        if (rangeFrom is { } from)
        {
            long to = from + body.Length - 1;
            content.Headers.ContentRange = total is { } length ? new ContentRangeHeaderValue(from, to, length) : new ContentRangeHeaderValue(from, to);
        }
        var response = new HttpResponseMessage(status) { Content = content };
        response.Headers.AcceptRanges.Add("bytes");
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }
        return Task.FromResult(response);
    };

    /// <summary>
    /// As <see cref="Bytes"/>, with <paramref name="body"/> read as it arrives; the answer promises <paramref name="length"/>
    /// bytes (<c>Content-Length</c>, none when null), as a server does before a body breaks off or runs on.
    /// </summary>
    public static Func<Task<HttpResponseMessage>> Streamed(HttpStatusCode status, Stream body, long? length, string? etag, long? rangeFrom = null,
        long? total = null) => () =>
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = length;
        if (rangeFrom is { } from && length is { } count)
        {
            content.Headers.ContentRange = total is { } size ? new ContentRangeHeaderValue(from, from + count - 1, size) : new ContentRangeHeaderValue(from, from + count - 1);
        }
        var response = new HttpResponseMessage(status) { Content = content };
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }
        return Task.FromResult(response);
    };

    /// <summary>A redirect to <paramref name="location"/>, as <c>/file-proxy</c> answers when a proxy node serves the file.</summary>
    public static Func<Task<HttpResponseMessage>> Redirect(string location) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return Task.FromResult(response);
    };

    /// <summary>Bytes of <paramref name="length"/> that differ from one position to the next.</summary>
    public static byte[] Source(int length, int seed = 1) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)((i * 31 + seed * 7) % 251))];

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>
/// A media body as a server sends it: at most <see cref="ChunkSize"/> bytes a read. It can break off with an
/// <see cref="IOException"/> after <see cref="BreakAfter"/> bytes, as a lost connection does, or stall there after
/// <see cref="StallAfter"/> bytes until the reader gives up.
/// </summary>
internal sealed class TestMediaStream(byte[] bytes) : Stream
{
    private readonly TaskCompletionSource _stalling = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _position;

    public int ChunkSize { get; init; } = 1024;

    public long? BreakAfter { get; init; }

    public long? StallAfter { get; init; }

    /// <summary>How many bytes were read so far.</summary>
    public long Delivered => Interlocked.Read(ref _position);

    /// <summary>Completes once a read waits at <see cref="StallAfter"/>.</summary>
    public Task Stalling => _stalling.Task;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position == BreakAfter)
        {
            throw new IOException("The connection broke off.");
        }
        if (_position == StallAfter)
        {
            _stalling.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        long end = Math.Min(bytes.Length, Math.Min(BreakAfter ?? long.MaxValue, StallAfter ?? long.MaxValue));
        int count = (int)Math.Min(Math.Min(buffer.Length, ChunkSize), end - _position);
        bytes.AsMemory((int)_position, count).CopyTo(buffer);
        Interlocked.Add(ref _position, count);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
