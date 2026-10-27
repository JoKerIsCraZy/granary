using System.Globalization;
using Granary.Core.Api.Requests;

namespace Granary.Core.ViewModels;

/// <summary>The groups "Soonest first" puts a watchlist title in; lower sorts first.</summary>
public enum WatchlistTitleRank
{
    Downloading,
    Dated,
    AwaitingApproval,
    NotRequested,
    Attention,
}

/// <summary>
/// How a watchlist title the library does not have reads on its card, as the web's <c>lib/watchlistTitles.ts</c> words it: a
/// short badge on the poster, and a caption that always says the whole status in words.
/// </summary>
/// <param name="Badge">"Downloading 43%", "Out Dec 18", "Awaiting approval".</param>
/// <param name="Caption">"Out Dec 18 · awaiting approval".</param>
/// <param name="Attention">TMDB no longer has the title as saved; the viewer has to find it again.</param>
/// <param name="Requestable">No active request, and the viewer may request it now.</param>
internal sealed record WatchlistTitleStatus(string Badge, string Caption, bool Attention, bool Requestable, WatchlistTitleRank Rank)
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    public static WatchlistTitleStatus Of(WatchlistTitle title, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(title);
        switch (title.Status)
        {
            case WatchlistTitleStatuses.NeedsReview:
                return new("Needs attention", "TMDB lists it twice", true, false, WatchlistTitleRank.Attention);
            case WatchlistTitleStatuses.Removed:
                return new("Not on TMDB", "No longer listed on TMDB", true, false, WatchlistTitleRank.Attention);
        }

        var request = title.Request;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        string? releasing = ReleaseDate(title) is { } release && release > today
            ? "Out " + release.ToString(release.Year == today.Year ? "MMM d" : "MMM d, yyyy", English)
            : null;

        if (request.Download is { } download)
        {
            string caption = RequestText.Download(download, now);
            string badge = download.Phase == "downloading" && RequestText.DownloadPercent(download) is { } percent
                ? $"Downloading {percent.ToString("0.##", English)}%"
                : caption.Split(" · ")[0];
            return new(badge, caption, false, false, WatchlistTitleRank.Downloading);
        }

        switch (RequestText.DisplayState(request.Status, null, request.State))
        {
            case null or RequestStates.Declined or RequestStates.Cancelled or RequestStates.Failed:
                string? refused = !request.Requestable && request.Reason is { Length: > 0 } reason
                    ? RequestText.ReasonLabel(reason).ToLowerInvariant()
                    : null;
                string? detail = refused ?? releasing?.ToLowerInvariant();
                return new("Not requested", detail is null ? "Not requested" : $"Not requested · {detail}", false, request.Requestable,
                    WatchlistTitleRank.NotRequested);
            case RequestStates.Processing:
                return new("Processing", "Processing", false, false, WatchlistTitleRank.Downloading);
            case RequestStates.Pending:
                return releasing is null
                    ? new("Awaiting approval", "Awaiting approval", false, false, WatchlistTitleRank.AwaitingApproval)
                    : new(releasing, $"{releasing} · awaiting approval", false, false, WatchlistTitleRank.Dated);
            case RequestStates.Approved:
                return releasing is null
                    ? new("Approved", "Approved · waiting for a download", false, false, WatchlistTitleRank.Dated)
                    : new(releasing, $"{releasing} · approved", false, false, WatchlistTitleRank.Dated);
            case { } state:
                string label = RequestText.StateLabel(state);
                return new(label, label, false, false, WatchlistTitleRank.Dated);
        }
    }

    /// <summary>TMDB lost the title as saved: needs review or removed.</summary>
    public static bool NeedsAttention(WatchlistTitle title) => title.Status is WatchlistTitleStatuses.NeedsReview or WatchlistTitleStatuses.Removed;

    /// <summary>
    /// "Soonest first": downloading, then by release date, then awaiting approval, then not requested, then needing attention.
    /// Within a group the earlier release first (unknown dates last), then the one added last.
    /// </summary>
    public static IReadOnlyList<WatchlistTitle> SoonestFirst(IEnumerable<WatchlistTitle> titles, DateTimeOffset now, TimeZoneInfo zone) =>
        [.. titles
            .OrderBy(title => Of(title, now, zone).Rank)
            .ThenBy(title => ReleaseDate(title) ?? DateOnly.MaxValue)
            .ThenByDescending(title => title.AddedAt)];

    /// <summary>The hint over the titles; the request and notification only while adding to the watchlist requests.</summary>
    public static string Hint(bool watchlistRequests) => "These titles aren't in the library yet, so they can't be played. " + (watchlistRequests
        ? "They've been requested for you. When one arrives, it moves to In your library and you get a notification."
        : "When one arrives, it moves to In your library.");

    // "1995-12-15"; anything else is no date.
    private static DateOnly? ReleaseDate(WatchlistTitle title) =>
        DateOnly.TryParseExact(title.ReleaseDate, "yyyy-MM-dd", English, DateTimeStyles.None, out var date) ? date : null;
}
