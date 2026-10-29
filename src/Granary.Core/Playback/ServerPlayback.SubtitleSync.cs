using System.Globalization;
using System.Text.Json;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Subtitles;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// Subtitle sync (SubtitleSyncTracker, docs/api-contract.md 9.8): the tracker follows the playing file and its syncable
// subtitles, the control lane's sync events reach it, and a subtitle whose timing changed is read again from its URL,
// which serves the new timing, as the web player reloads its cues.
public sealed partial class ServerPlayback
{
    public SubtitleSyncTracker? SubtitleSync { get; }

    public event EventHandler<string>? SubtitleTimingChanged;

    public event EventHandler<string>? SubtitleCuesReloaded;

    public string? SubtitleSyncKey =>
        SubtitleTrack is { } id && _plan?.Subtitle.Inventory.FirstOrDefault(i => i.TrackId == id)?.SyncKey is { Length: > 0 } key ? key : null;

    private void FollowSubtitleSync(PlaybackPlan plan)
    {
        if (SubtitleSync is not { } sync || _context is not { } context)
        {
            return;
        }
        string fileId = plan.EffectiveMediaFileId.Length > 0 ? plan.EffectiveMediaFileId : context.Version.FileId;
        if (fileId.Length > 0)
        {
            sync.Follow(context.Server.BaseUri, fileId, plan.Subtitle.Inventory.Select(i => i.SyncKey ?? ""));
        }
    }

    // From the lane's thread to the UI thread: only the session that plays, and only its file's subtitles.
    private void OnControlEvent(string sessionId, PlaybackControlEvent controlEvent)
    {
        if (SubtitleSync is null || controlEvent.Name is not (PlaybackControlEvents.SubtitleSyncUpdated or PlaybackControlEvents.SubtitleTimingChanged))
        {
            return;
        }
        _dispatcher.Post(() =>
        {
            if (_stopping || _progress?.SessionId != sessionId || SubtitleSync is not { FileId: { } fileId } sync)
            {
                return;
            }
            var payload = controlEvent.Payload;
            if (payload.ValueKind != JsonValueKind.Object || Id(payload, "file_id") != fileId || SyncKey(payload) is not { } key)
            {
                return;
            }
            if (controlEvent.Name == PlaybackControlEvents.SubtitleTimingChanged)
            {
                sync.OnTimingChanged(key);
                return;
            }
            try
            {
                if (payload.TryGetProperty("timing", out var timing) && payload.TryGetProperty("job", out var job)
                    && timing.Deserialize(SubtitleSyncJsonContext.Default.SubtitleTiming) is { } parsedTiming
                    && job.Deserialize(SubtitleSyncJsonContext.Default.SubtitleSyncJob) is { Id.Length: > 0, Status.Length: > 0 } parsedJob)
                {
                    sync.OnSyncUpdated(key, parsedTiming, parsedJob);
                }
            }
            catch (JsonException)
            {
                _logger.LogDebug("A subtitle sync event could not be read");
            }
        });
    }

    // A track read before the change reads again when picked; the one that shows reads again now, as its URL serves the new
    // timing. A burned-in subtitle is in the picture and keeps its timing until the next plan.
    private void OnSyncedTimingChanged(object? sender, string key)
    {
        if (_stopping || _plan is not { } plan)
        {
            return;
        }
        SubtitleTimingChanged?.Invoke(this, key);
        Task<int?>? showing = null;
        foreach (var item in plan.Subtitle.Inventory.Where(i => i.SyncKey == key))
        {
            if (_loadedSubtitles.Remove(item.TrackId, out var read) && item.TrackId == SubtitleTrack)
            {
                showing = read;
            }
        }
        if (showing is not null && plan.Subtitle.Mode != SubtitleModes.BurnIn)
        {
            _ = ReloadSubtitleAsync(key, showing, _loadId);
        }
    }

    // A read still on its way may bring the cues from before the change: it is waited for, then the subtitle is read again
    // through a route of its own, so mpv reads it anew. The old track goes whatever shows by then (another pick, none, or a
    // read that failed), as long as its load plays; the screen hears of the new cues only when they show.
    private async Task ReloadSubtitleAsync(string key, Task<int?> previous, long loadId)
    {
        try
        {
            await ReloadSubtitleCoreAsync(key, previous, loadId);
        }
        catch (Exception ex)
        {
            // Subtitles are not playback: the stream plays on with what it has.
            _logger.LogError(ex, "Reading the subtitle again failed");
        }
    }

    private async Task ReloadSubtitleCoreAsync(string key, Task<int?> previous, long loadId)
    {
        _logger.LogInformation("Reading the subtitle again: its timing changed");
        int? oldStream = await previous;
        if (_stopping || loadId != _loadId)
        {
            // That load ended, and its tracks with it.
            return;
        }
        await ShowSubtitlesAsync(loadId);
        if (_stopping || loadId != _loadId)
        {
            return;
        }
        int? newStream = SubtitleTrack is { } track && _loadedSubtitles.TryGetValue(track, out var read) && read.IsCompletedSuccessfully
            ? read.Result
            : null;
        if (oldStream is { } old && old != newStream)
        {
            _engine.RemoveSubtitle(old);
        }
        if (newStream is { } stream && stream != oldStream && SubtitleSyncKey == key)
        {
            SubtitleCuesReloaded?.Invoke(this, key);
        }
    }

    // The payload's sync key; an older server's timing event names only the stored subtitle.
    private static string? SyncKey(JsonElement payload)
    {
        if (payload.TryGetProperty("sync_key", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() is { Length: > 0 } text)
        {
            return text;
        }
        return Id(payload, "subtitle_id") is { } stored ? "stored-" + stored : null;
    }

    // An id the server sends as a number (file_id, subtitle_id) or as text.
    private static string? Id(JsonElement payload, string name) =>
        !payload.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number.ToString(CultureInfo.InvariantCulture)
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : null;
}
