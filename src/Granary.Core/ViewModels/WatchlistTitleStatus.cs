using System.Globalization;
using Granary.Core.Api.Requests;
using Granary.Core.Texts;

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
                return new(AppText.Get("web.library.watchlist.statusNeedsAttention"), AppText.Get("web.library.watchlist.statusListedTwice"),
                    true, false, WatchlistTitleRank.Attention);
            case WatchlistTitleStatuses.Removed:
                return new(AppText.Get("web.library.watchlist.statusNotOnTmdb"), AppText.Get("web.library.watchlist.statusNoLongerListed"),
                    true, false, WatchlistTitleRank.Attention);
        }

        var request = title.Request;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        string? releaseDay = ReleaseDate(title) is { } release && release > today
            ? release.ToString(release.Year == today.Year ? "MMM d" : "MMM d, yyyy", English)
            : null;
        string? releasing = releaseDay is null ? null : AppText.Get("web.library.watchlist.statusOut", ("date", releaseDay));

        if (request.Download is { } download)
        {
            string caption = RequestText.Download(download, now);
            string badge = download.Phase == "downloading" && RequestText.DownloadPercent(download) is { } percent
                ? AppText.Get("web.library.watchlist.statusDownloading", ("percent", percent.ToString("0.##", English)))
                : caption.Split(" · ")[0];
            return new(badge, caption, false, false, WatchlistTitleRank.Downloading);
        }

        switch (RequestText.DisplayState(request.Status, null, request.State))
        {
            case null or RequestStates.Declined or RequestStates.Cancelled or RequestStates.Failed:
                // As the web, the reason and the day in lower case, by the rules of the language on screen.
                var culture = AppText.Language.Culture;
                string notRequested = AppText.Get("web.library.watchlist.statusNotRequested");
                string caption = !request.Requestable && request.Reason is { Length: > 0 } reason
                    ? AppText.Get("web.library.watchlist.statusNotRequestedReason", ("reason", RequestText.ReasonLabel(reason).ToLower(culture)))
                    : releaseDay is not null
                        ? AppText.Get("web.library.watchlist.statusNotRequestedOut", ("date", releaseDay.ToLower(culture)))
                        : notRequested;
                return new(notRequested, caption, false, request.Requestable, WatchlistTitleRank.NotRequested);
            case RequestStates.Processing:
                string processing = AppText.Get("web.requests.status.processing");
                return new(processing, processing, false, false, WatchlistTitleRank.Downloading);
            case RequestStates.Pending:
                return releasing is null
                    ? new(AppText.Get("web.library.watchlist.statusAwaitingApproval"), AppText.Get("web.library.watchlist.statusAwaitingApproval"),
                        false, false, WatchlistTitleRank.AwaitingApproval)
                    : new(releasing, AppText.Get("web.library.watchlist.statusOutAwaitingApproval", ("date", releaseDay)),
                        false, false, WatchlistTitleRank.Dated);
            case RequestStates.Approved:
                return releasing is null
                    ? new(AppText.Get("web.requests.status.approved"), AppText.Get("web.library.watchlist.statusApprovedWaiting"),
                        false, false, WatchlistTitleRank.Dated)
                    : new(releasing, AppText.Get("web.library.watchlist.statusOutApproved", ("date", releaseDay)),
                        false, false, WatchlistTitleRank.Dated);
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
    public static string Hint(bool watchlistRequests) => watchlistRequests
        ? AppText.Get("web.library.watchlist.titlesHintRequested")
        : AppText.Get("web.library.watchlist.titlesHint");

    // "1995-12-15"; anything else is no date.
    private static DateOnly? ReleaseDate(WatchlistTitle title) =>
        DateOnly.TryParseExact(title.ReleaseDate, "yyyy-MM-dd", English, DateTimeStyles.None, out var date) ? date : null;
}
