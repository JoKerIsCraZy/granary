using System.Text.Json;

namespace Granary.Core.Api.Playback;

/// <summary>
/// Editing a file's markers (<c>PUT /api/v2/markers/files/{file_id}</c>, the <c>marker_edit</c> permission): the
/// segments given are stored as manual ones, at once; the answer is the file's markers as they are now.
/// </summary>
public sealed class MarkersApi(SiloHttp http)
{
    /// <summary>
    /// Stores <paramref name="changes"/> for <paramref name="fileId"/>: a kind with a range sets it, a kind with null
    /// clears it, and a kind left out keeps what it has, so a marker from a scan is not rewritten as manual because
    /// another one was edited.
    /// </summary>
    public Task<FileMarkers> SetAsync(Uri server, string fileId, IReadOnlyDictionary<string, WatchMarker?> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        string path = "api/v2/markers/files/" + SiloHttp.PathSegment(fileId, nameof(fileId));
        return http.SendAsync(HttpMethod.Put, server, path, SiloAuth.Profile, Body(changes),
            PlaybackJsonContext.Default.JsonElement, PlaybackJsonContext.Default.FileMarkers, cancellationToken);
    }

    // Written by hand: an omitted kind and a null one mean different things, and the generated writer leaves nulls out.
    internal static JsonElement Body(IReadOnlyDictionary<string, WatchMarker?> changes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (string kind in new[] { WatchMarkerKinds.Intro, WatchMarkerKinds.Credits, WatchMarkerKinds.Recap, WatchMarkerKinds.Preview })
            {
                if (!changes.TryGetValue(kind, out var range))
                {
                    continue;
                }
                if (range is null)
                {
                    writer.WriteNull(kind);
                    continue;
                }
                writer.WriteStartObject(kind);
                writer.WriteNumber("start_seconds", Math.Round(range.StartSeconds, 3));
                writer.WriteNumber("end_seconds", Math.Round(range.EndSeconds, 3));
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}

/// <summary>A file's effective markers: one range per kind for older clients, and every occurrence.</summary>
public sealed record FileMarkers
{
    public string FileId { get; init => field = value ?? ""; } = "";

    public FileMarkerRange? Intro { get; init; }

    public FileMarkerRange? Credits { get; init; }

    public FileMarkerRange? Recap { get; init; }

    public FileMarkerRange? Preview { get; init; }

    public IReadOnlyList<WatchMarkerSegment> MarkerSegments { get; init => field = value ?? []; } = [];

    /// <summary>The markers as a file version carries them, for <see cref="Playback.PlaybackMarker.Of"/>.</summary>
    public WatchFileVersion AsVersion(string fileId) => new()
    {
        FileId = fileId,
        MarkerSegments = MarkerSegments,
        Intro = Intro?.AsMarker(),
        Credits = Credits?.AsMarker(),
        Recap = Recap?.AsMarker(),
        Preview = Preview?.AsMarker(),
    };
}

/// <summary>A kind's range; both ends are left out when the file has none of that kind.</summary>
public sealed record FileMarkerRange
{
    public double? StartSeconds { get; init; }

    public double? EndSeconds { get; init; }

    public WatchMarker? AsMarker() => StartSeconds is { } start && EndSeconds is { } end && end > start ? new WatchMarker(start, end) : null;
}
