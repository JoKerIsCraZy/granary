using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// Whether one of the profile's libraries has box sets on (docs/api-contract.md, 7.18), which a movie's page asks before it
/// reads its box set, as the web's <c>useItemBoxSet</c> does: a server without box sets, or with them off everywhere, is not
/// asked on every movie. One read of <c>GET /api/v2/user/libraries</c> per server and profile, shared by every page that asks
/// meanwhile and kept for <see cref="KeepFor"/> (the web's staleTime), as <see cref="SeriesAirings"/> keeps its answers; a
/// change of server, profile or session forgets it. The list is the server's, as the web's
/// <c>useAvailableUserLibraries</c>: a library the profile hid from its own sidebar still counts, since its movies can still be
/// opened from search or a link. Any failure answers no and is not kept, so the next page asks again. One for the whole app.
/// </summary>
public sealed class BoxSetLibraries
{
    /// <summary>How long an answer is used again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);

    /// <summary>How long a read may take before every page waiting for it is told no.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    private readonly CatalogApi _catalog;
    private readonly ILogger<BoxSetLibraries> _logger;
    private readonly KeptAnswers<Key, bool> _answers;

    public BoxSetLibraries(CatalogApi catalog, ISessionManager session, TimeProvider time, ILogger<BoxSetLibraries> logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        _catalog = catalog;
        _logger = logger;
        // A read goes out as whichever profile acts when it is sent: an answer that came while another acts may be theirs.
        _answers = new(time, KeepFor, GiveUpAfter, key => SeriesAirings.Acts(session, key.Server, key.Profile));
        session.Changed += (_, _) => _answers.Clear();
    }

    /// <summary>Whether a library of <paramref name="server"/> that <paramref name="profileId"/> sees has box sets on; no on any failure.</summary>
    public async Task<bool> AnyAsync(Uri server, string? profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        try
        {
            return await _answers.GetAsync(new Key(server.AbsoluteUri, profileId), giveUp => ReadAsync(server, giveUp), cancellationToken);
        }
        // Whatever the read ends with, the box set row only stays out: the page's other rows go on.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Reading which libraries have box sets failed: {Failure}", ApiFailureText.Describe(ex));
            }
            return false;
        }
    }

    // Given up only by its own time limit, not by the page that asked first: others may be waiting for the same read.
    private async Task<bool> ReadAsync(Uri server, CancellationToken giveUp)
    {
        var libraries = await _catalog.ListLibrariesAsync(server, giveUp);
        return libraries.Items.Any(library => library.BoxSetsEnabled);
    }

    private readonly record struct Key(string Server, string? Profile);
}
