using System.Globalization;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>A title on TMDB: its page, and its poster where the server could name one.</summary>
public sealed record TmdbTitle(Uri Page, Uri? Poster);

/// <summary>
/// The TMDB page and poster of the title that plays, for the Discord status, read through the server so Granary needs no
/// TMDB key of its own: the catalog item's <c>tmdb_id</c>, then the requests detail's <c>poster_path</c>
/// (docs/api-contract.md, 7.6 and 7.15). Only TMDB's own addresses come out, never one of the server's, which would show
/// its address to everyone who looks at the status. An answer is kept for the run, none too, for the profile it was read
/// as: a profile that may not see a title, as a child's, is refused where another is not. A server that could not be
/// reached or was busy is asked again for the next title.
/// </summary>
public sealed class TmdbTitleLookup(CatalogApi catalog, RequestsApi requests, ISessionManager session, ILogger<TmdbTitleLookup> logger)
{
    /// <summary>The poster's TMDB size; Discord shows it small, but sharp on a large screen.</summary>
    public const string PosterSize = "w500";

    private const string Site = "https://www.themoviedb.org/";

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Server, string? Profile, PlaybackMediaKind Kind, string Id), Task<Lookup>> _reads = [];

    /// <summary>
    /// The page and poster of the movie <paramref name="catalogId"/>, or of the series <paramref name="catalogId"/> of an
    /// episode; null when the server cannot say. Throws only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<TmdbTitle?> FindAsync(PlaybackMediaKind kind, string catalogId, CancellationToken cancellationToken = default)
    {
        if (kind is not (PlaybackMediaKind.Movie or PlaybackMediaKind.Episode) || string.IsNullOrWhiteSpace(catalogId)
            || session.ActiveServer is not { } server)
        {
            return null;
        }
        // The profile the requests go out as (SiloAuth.Profile takes the session's).
        string? profile = session.ProfileId;
        Task<Lookup> read;
        lock (_gate)
        {
            var key = (server.Id, profile, kind, catalogId);
            if (!_reads.TryGetValue(key, out read!) || read is { IsCompletedSuccessfully: true, Result.IsFinal: false })
            {
                // Off the caller's thread, which is the UI's, and outside the lock.
                read = Task.Run(() => ReadAsProfileAsync(server.Id, server.BaseUri, profile, kind, catalogId), CancellationToken.None);
                _reads[key] = read;
            }
        }
        return (await read.WaitAsync(cancellationToken).ConfigureAwait(false)).Title;
    }

    // An answer read while the session went to another profile or server may be that one's: it is neither kept nor shown.
    private async Task<Lookup> ReadAsProfileAsync(string serverId, Uri server, string? profile, PlaybackMediaKind kind, string catalogId)
    {
        var lookup = await ReadAsync(server, kind, catalogId).ConfigureAwait(false);
        return session.ActiveServer?.Id == serverId && session.ProfileId == profile ? lookup : new Lookup(null, IsFinal: false);
    }

    private async Task<Lookup> ReadAsync(Uri server, PlaybackMediaKind kind, string catalogId)
    {
        try
        {
            var item = await catalog.GetItemAsync(server, catalogId).ConfigureAwait(false);
            if (!long.TryParse(item.TmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out long tmdbId) || tmdbId <= 0)
            {
                return new Lookup(null, IsFinal: true);
            }
            bool isMovie = kind == PlaybackMediaKind.Movie;
            var page = new Uri(Site + (isMovie ? "movie/" : "tv/") + tmdbId.ToString(CultureInfo.InvariantCulture));
            try
            {
                var detail = await requests.GetDetailAsync(server, isMovie ? "movie" : "series", tmdbId).ConfigureAwait(false);
                // A TMDB image only: a path that is not one gives no poster.
                return new Lookup(new TmdbTitle(page, TmdbImages.Poster(detail.PosterPath, PosterSize)), IsFinal: true);
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex))
            {
                // Requests may be off on the server: the title shows with Granary's own picture.
                logger.LogDebug("The playing title's poster could not be read: {Failure}", ApiFailureText.Describe(ex));
                return new Lookup(new TmdbTitle(page, null), IsFinal(ex));
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            logger.LogInformation("The playing title's TMDB entry could not be read: {Failure}", ApiFailureText.Describe(ex));
            return new Lookup(null, IsFinal(ex));
        }
        catch (Exception ex)
        {
            // Nobody waits for this read: the status goes on without the poster.
            logger.LogError(ex, "Reading the playing title's TMDB entry failed unexpectedly");
            return new Lookup(null, IsFinal: true);
        }
    }

    // The server's answer holds for the run; one that could not come, or a busy server's, does not.
    private static bool IsFinal(Exception exception) =>
        exception is JsonException or SiloApiException { Status: < 500 and not 429 };

    private sealed record Lookup(TmdbTitle? Title, bool IsFinal);
}
