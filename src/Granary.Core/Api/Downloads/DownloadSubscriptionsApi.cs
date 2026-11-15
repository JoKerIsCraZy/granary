using System.Net;

namespace Granary.Core.Api.Downloads;

/// <summary>
/// The server's series monitors (downloads-api.md, 8; docs/api-contract.md, 14.8), as Silo's Apple app calls them
/// (<c>APIv2Client+DownloadSubscriptions.swift</c>). A monitor belongs to this account, profile and PC (the device id Granary
/// sends); a call is bound to the account and the profile it was made for (<see cref="SiloRequestOptions.Account"/>): profile
/// ids need not differ between accounts, so a call that waited while another account signed in throws
/// <see cref="ProfileChangedException"/> unsent, as for another profile. The forms without the account are bound to the profile
/// alone, for callers that do not pass it yet. An answer that does not describe what was asked, and a list that does not end
/// whole, is no answer (<see cref="HttpRequestException"/> with <see cref="HttpRequestError.InvalidResponse"/>):
/// nothing is taken from it.
/// </summary>
public sealed class DownloadSubscriptionsApi(SiloHttp http)
{
    /// <summary>Monitors per list page and episodes a sync page looks at, the server's largest.</summary>
    public const int PageLimit = 100;

    // The most pages a list reads; one that goes on is not whole.
    private const int MaxPages = 1000;

    private const string Subscriptions = "api/v2/downloads/subscriptions";

    /// <summary>Every monitor of this profile and PC, all pages; one that ends early, repeats a cursor or a monitor, or holds one that cannot be kept, is not whole.</summary>
    public async Task<IReadOnlyList<DownloadSubscription>> ListAsync(Uri server, string accountId, string profileId, CancellationToken cancellationToken)
    {
        var monitors = new List<DownloadSubscription>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            string path = SiloHttp.Invariant($"{Subscriptions}?limit={PageLimit}") + (cursor is null ? "" : "&cursor=" + SiloHttp.Escape(cursor));
            var read = await http.GetAsAccountProfileAsync(server, path, accountId, profileId, DownloadsJsonContext.Default.DownloadSubscriptionPage,
                cancellationToken).ConfigureAwait(false);
            if (read.Items.Count > PageLimit)
            {
                throw NotWhole();
            }
            foreach (var monitor in read.Items)
            {
                if (!monitor.IsUsable || !ids.Add(monitor.Id))
                {
                    throw NotWhole();
                }
                monitors.Add(monitor);
            }
            // A page a cursor asked for has to say whether more follow; only the first may leave its paging out.
            if (read.Page is null && page > 0)
            {
                throw NotWhole();
            }
            if (read.Page is not { HasMore: true })
            {
                return monitors;
            }
            if (read.Page.NextCursor is not { Length: > 0 } next || read.Items.Count == 0 || !cursors.Add(next))
            {
                throw NotWhole();
            }
            cursor = next;
        }
        throw NotWhole();
    }

    /// <summary>One monitor as it is now, with its current validator.</summary>
    public async Task<DownloadSubscription> GetAsync(Uri server, string accountId, string profileId, string subscriptionId,
        CancellationToken cancellationToken)
    {
        var monitor = await http.GetAsAccountProfileAsync(server, Entry(subscriptionId), accountId, profileId,
            DownloadsJsonContext.Default.DownloadSubscription, cancellationToken).ConfigureAwait(false);
        return Checked(monitor, subscriptionId, null);
    }

    /// <summary>As the other <c>GetAsync</c>, bound to the profile alone; it goes once every caller passes the account.</summary>
    public async Task<DownloadSubscription> GetAsync(Uri server, string profileId, string subscriptionId, CancellationToken cancellationToken)
    {
        var monitor = await http.GetAsProfileAsync(server, Entry(subscriptionId), profileId, DownloadsJsonContext.Default.DownloadSubscription,
            cancellationToken).ConfigureAwait(false);
        return Checked(monitor, subscriptionId, null);
    }

    /// <summary>
    /// Starts monitoring a series, sent exactly once: the server answers with the new monitor, or the one this PC has for the
    /// series already, unchanged. After an answer that did not come, read the list instead of sending again.
    /// </summary>
    public async Task<DownloadSubscription> CreateAsync(Uri server, string accountId, string profileId, CreateSubscriptionBody body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var monitor = await http.SendAsAccountProfileAsync(HttpMethod.Post, server, Subscriptions, accountId, profileId, body,
            DownloadsJsonContext.Default.CreateSubscriptionBody, DownloadsJsonContext.Default.DownloadSubscription, cancellationToken).ConfigureAwait(false);
        return Checked(monitor, null, body.SeriesId);
    }

    /// <summary>As the other <c>CreateAsync</c>, bound to the profile alone; it goes once every caller passes the account.</summary>
    public async Task<DownloadSubscription> CreateAsync(Uri server, string profileId, CreateSubscriptionBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var monitor = await http.SendAsProfileAsync(HttpMethod.Post, server, Subscriptions, profileId, body,
            DownloadsJsonContext.Default.CreateSubscriptionBody, DownloadsJsonContext.Default.DownloadSubscription, cancellationToken).ConfigureAwait(false);
        return Checked(monitor, null, body.SeriesId);
    }

    /// <summary>
    /// Changes a monitor under the validator it was read with. One that changed since answers 412 (or 409 while the server
    /// applied another change): read it again, never send the same change over the new one unasked.
    /// </summary>
    public async Task<DownloadSubscription> UpdateAsync(Uri server, string accountId, string profileId, string subscriptionId, string etag,
        UpdateSubscriptionBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var monitor = await http.SendIfMatchAsAccountProfileAsync(HttpMethod.Patch, server, Entry(subscriptionId), accountId, profileId, etag, body,
            DownloadsJsonContext.Default.UpdateSubscriptionBody, DownloadsJsonContext.Default.DownloadSubscription, cancellationToken).ConfigureAwait(false);
        return Checked(monitor, subscriptionId, null);
    }

    /// <summary>As the other <c>UpdateAsync</c>, bound to the profile alone; it goes once every caller passes the account.</summary>
    public async Task<DownloadSubscription> UpdateAsync(Uri server, string profileId, string subscriptionId, string etag, UpdateSubscriptionBody body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var monitor = await http.SendIfMatchAsProfileAsync(HttpMethod.Patch, server, Entry(subscriptionId), profileId, etag, body,
            DownloadsJsonContext.Default.UpdateSubscriptionBody, DownloadsJsonContext.Default.DownloadSubscription, cancellationToken).ConfigureAwait(false);
        return Checked(monitor, subscriptionId, null);
    }

    /// <summary>Stops a monitor under its validator; the downloads it registered stay. One the server no longer has (404) is gone already.</summary>
    public async Task DeleteAsync(Uri server, string accountId, string profileId, string subscriptionId, string etag, CancellationToken cancellationToken)
    {
        string path = Entry(subscriptionId);
        await GoneIsDoneAsync(http.SendIfMatchAsAccountProfileAsync(HttpMethod.Delete, server, path, accountId, profileId, etag, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>As the other <c>DeleteAsync</c>, bound to the profile alone; it goes once every caller passes the account.</summary>
    public async Task DeleteAsync(Uri server, string profileId, string subscriptionId, string etag, CancellationToken cancellationToken)
    {
        string path = Entry(subscriptionId);
        await GoneIsDoneAsync(http.SendIfMatchAsProfileAsync(HttpMethod.Delete, server, path, profileId, etag, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// One page of a sync: the server registers the episodes of this page the monitor takes. Every page of one sync names the
    /// validator read before its first; <paramref name="cursor"/> continues it. A monitor that changed meanwhile answers 409.
    /// </summary>
    public async Task<SubscriptionSyncPage> SyncPageAsync(Uri server, string accountId, string profileId, string subscriptionId, string etag,
        string? cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionId);
        ArgumentException.ThrowIfNullOrEmpty(etag);
        string path = SiloHttp.Invariant($"{Subscriptions}/sync?limit={PageLimit}") + (cursor is null ? "" : "&cursor=" + SiloHttp.Escape(cursor));
        var page = await http.SendAsAccountProfileAsync(HttpMethod.Post, server, path, accountId, profileId, new SubscriptionSyncBody(subscriptionId, etag),
            DownloadsJsonContext.Default.SubscriptionSyncBody, DownloadsJsonContext.Default.SubscriptionSyncPage, cancellationToken).ConfigureAwait(false);
        if (page.SubscriptionId != subscriptionId || page.Registered < 0)
        {
            throw Unexpected();
        }
        return page;
    }

    // A delete of a monitor the server no longer has (404) is done: deleted before, from this PC or another client of the profile.
    private static async Task GoneIsDoneAsync(Task delete)
    {
        try
        {
            await delete.ConfigureAwait(false);
        }
        catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            // Gone already.
        }
    }

    // A monitor answer must be a usable monitor, and the one (or the series') asked about.
    private static DownloadSubscription Checked(DownloadSubscription monitor, string? id, string? seriesId)
    {
        if (!monitor.IsUsable || (id is not null && monitor.Id != id) || (seriesId is not null && monitor.SeriesId != seriesId))
        {
            throw Unexpected();
        }
        return monitor;
    }

    private static string Entry(string subscriptionId) => Subscriptions + "/" + SiloHttp.PathSegment(subscriptionId, nameof(subscriptionId));

    private static HttpRequestException NotWhole() => new(HttpRequestError.InvalidResponse, "The server's list of monitored series did not end whole.");

    private static HttpRequestException Unexpected() => new(HttpRequestError.InvalidResponse, "The server's monitoring answer did not describe what was asked.");
}
