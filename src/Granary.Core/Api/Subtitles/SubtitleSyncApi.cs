namespace Granary.Core.Api.Subtitles;

/// <summary>
/// Aligning a file's subtitles to its audio, as Silo's web player and apps do (docs/api-contract.md, 9.8): the server
/// says whether it can, lists the timing and latest sync job of every subtitle it can sync (stored ones and files next
/// to the media) by their sync key, starts a sync, and replaces a subtitle's timing at the version it was read at.
/// </summary>
public sealed class SubtitleSyncApi(SiloHttp http)
{
    /// <summary>Whether the server aligns subtitles to audio, and this viewer may.</summary>
    public Task<SubtitleSyncStatus> GetStatusAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/subtitles/sync/status", SiloAuth.Profile, SubtitleSyncJsonContext.Default.SubtitleSyncStatus, cancellationToken);

    /// <summary>Every subtitle of <paramref name="mediaFileId"/> the server can sync, stored ones first.</summary>
    public async Task<IReadOnlyList<SubtitleSyncState>> ListAsync(Uri server, string mediaFileId, CancellationToken cancellationToken = default)
    {
        var list = await http.GetAsync(server, FilePath(mediaFileId), SiloAuth.Profile, SubtitleSyncJsonContext.Default.SubtitleSyncList,
            cancellationToken).ConfigureAwait(false);
        return list.Subtitles;
    }

    /// <summary>One subtitle's timing and latest job, with the version <see cref="SetTimingAsync"/> takes.</summary>
    public async Task<Versioned<SubtitleSyncState>> ReadAsync(Uri server, string mediaFileId, string key, CancellationToken cancellationToken = default)
    {
        var read = await http.GetVersionedAsync(server, KeyPath(mediaFileId, key), SiloAuth.Profile, SubtitleSyncJsonContext.Default.SubtitleSyncEnvelope,
            cancellationToken).ConfigureAwait(false);
        return new(read.Value.Subtitle, read.Version);
    }

    /// <summary>Starts a sync of the subtitle, or answers its running one, and the subtitle with that job.</summary>
    public async Task<SubtitleSyncState> StartAsync(Uri server, string mediaFileId, string key, CancellationToken cancellationToken = default)
    {
        var started = await http.SendAsync(HttpMethod.Post, server, KeyPath(mediaFileId, key), SiloAuth.Profile,
            SubtitleSyncJsonContext.Default.SubtitleSyncEnvelope, cancellationToken).ConfigureAwait(false);
        return started.Subtitle;
    }

    /// <summary>
    /// Replaces the subtitle's timing at <paramref name="version"/> (from <see cref="ReadAsync"/>); one changed since
    /// answers 412. <see cref="SubtitleTiming.Identity"/> restores the original timing.
    /// </summary>
    public async Task<SubtitleSyncState> SetTimingAsync(Uri server, string mediaFileId, string key, string version, SubtitleTiming timing,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(timing);
        var written = await http.SendIfMatchAsync(HttpMethod.Put, server, KeyPath(mediaFileId, key) + "/timing", SiloAuth.Profile, version,
            new SubtitleTimingBody { OffsetMs = timing.OffsetMs, Scale = timing.Scale }, SubtitleSyncJsonContext.Default.SubtitleTimingBody,
            SubtitleSyncJsonContext.Default.SubtitleSyncEnvelope, cancellationToken).ConfigureAwait(false);
        return written.Value.Subtitle;
    }

    private static string FilePath(string mediaFileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFileId);
        return $"api/v2/subtitles/{Uri.EscapeDataString(mediaFileId)}/sync";
    }

    private static string KeyPath(string mediaFileId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return $"{FilePath(mediaFileId)}/{Uri.EscapeDataString(key)}";
    }
}
