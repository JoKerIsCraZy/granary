namespace Granary.Core.Api.Downloads;

/// <summary>
/// Progress made without a playback session (downloads-api.md, 7; docs/api-contract.md, 14.7): what a download played from
/// this PC recorded goes to the server, and the profile's progress is read back. Both calls are bound to the profile the
/// progress belongs to: while the server acts as another one, they throw <see cref="ProfileChangedException"/> unsent.
/// </summary>
public sealed class ProgressSyncApi(SiloHttp http)
{
    /// <summary>The most items one request takes.</summary>
    public const int MaxItems = 100;

    // The server's largest page.
    private const int PageLimit = 200;

    /// <summary>
    /// Sends <paramref name="body"/> once. The server answers 200 with a result for each item; it is never sent again by
    /// this call, whatever happens to the answer.
    /// </summary>
    public Task<ProgressSyncResult> SyncAsync(Uri server, string profileId, ProgressSyncBody body, CancellationToken cancellationToken) =>
        http.SendAsProfileAsync(HttpMethod.Post, server, "api/v2/sync/progress", profileId, body, DownloadsJsonContext.Default.ProgressSyncBody,
            DownloadsJsonContext.Default.ProgressSyncResult, cancellationToken);

    /// <summary>One page of the profile's progress, newest change first; <paramref name="cursor"/> continues a listing.</summary>
    public Task<ProgressListPage> ListAsync(Uri server, string profileId, string? cursor, CancellationToken cancellationToken)
    {
        string path = SiloHttp.Invariant($"api/v2/progress?limit={PageLimit}") + (cursor is null ? "" : "&cursor=" + SiloHttp.Escape(cursor));
        return http.GetAsProfileAsync(server, path, profileId, DownloadsJsonContext.Default.ProgressListPage, cancellationToken);
    }
}
