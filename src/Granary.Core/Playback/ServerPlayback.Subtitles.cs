using System.Globalization;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// Subtitles (docs/api-contract.md, 8.8 and 9.6), as Silo's Apple client handles them: the plan's track is shown once
// the stream plays, from the file itself on a direct play, else from its sidecar through the media proxy. A pick
// switches in the player where it can (any sidecar, an embedded track of a direct play, off) and is replanned with
// track_change otherwise, for one to leave a burn-in. Every pick is saved for the series.
public sealed partial class ServerPlayback
{
    // The sidecars read, or being read, into the load that plays, by track: picking one again shares its read. And the
    // routes they came through.
    private readonly Dictionary<string, Task<int?>> _loadedSubtitles = [];
    private readonly List<IMediaRoute> _subtitleRoutes = [];

    // The viewer's latest subtitle pick until a stream shows it (Off is a pick too); it names the plan's file.
    private (string FileId, string? TrackId)? _pendingSubtitle;

    // Nudges come in runs: the delay is saved once they settle, as the web player does.
    private static readonly TimeSpan SubtitleDelaySettle = TimeSpan.FromMilliseconds(600);

    // A stored subtitle's track names it so (sync_key); an older server's only in its URL.
    private const string StoredSyncKeyPrefix = "stored-";
    private const string StoredIdParameter = "downloaded_subtitle_id";

    // A subtitle the viewer added until a plan lists it, and whether a plan was asked for since.
    private string? _addedSubtitle;
    private bool _addedSubtitleAsked;
    private const string SubtitleDelayKey = "player.subtitle_sync_ms";

    // Where the shown track's cues sit against the player's clock (sidecars: -timeline offset), and the viewer's delay.
    private double _subtitleBaseSeconds;
    private int _subtitleDelayMs;
    private ITimer? _subtitleDelaySave;
    private bool _deviceDelayCleared;

    public int SubtitleDelayMs => _subtitleDelayMs;

    public void SetSubtitleDelay(int milliseconds)
    {
        int delay = Math.Clamp(milliseconds, -PlaybackPreferencesReader.MaxSubtitleDelayMs, PlaybackPreferencesReader.MaxSubtitleDelayMs);
        if (_context is null || _stopping || delay == _subtitleDelayMs)
        {
            return;
        }
        _subtitleDelayMs = delay;
        ApplySubtitleDelay();
        _subtitleDelaySave?.Dispose();
        _subtitleDelaySave = _time.CreateTimer(_ => _dispatcher.Post(SaveSubtitleDelayNow), null, SubtitleDelaySettle, Timeout.InfiniteTimeSpan);
    }

    private void ApplySubtitleDelay() => _engine.SetSubtitleDelay(_subtitleBaseSeconds + _subtitleDelayMs / 1000.0);

    // The profile's value, which follows the profile to every device, as the web writes it. A value this device kept
    // for itself would outrank it, so that one is cleared, once.
    private void SaveSubtitleDelayNow()
    {
        if (_subtitleDelaySave is null)
        {
            return;
        }
        _subtitleDelaySave.Dispose();
        _subtitleDelaySave = null;
        var context = _context!;
        using var document = JsonDocument.Parse(_subtitleDelayMs.ToString(CultureInfo.InvariantCulture));
        var value = document.RootElement.Clone();
        bool clearDevice = context.Preferences.SubtitleDelayOnDevice && !_deviceDelayCleared;
        _deviceDelayCleared |= clearDevice;
        SaveChoice("subtitle delay", async cancellationToken =>
        {
            await _settings.SetValueAsync(context.Server.BaseUri, SubtitleDelayKey, value, SettingScopes.Profile, cancellationToken);
            if (clearDevice)
            {
                try
                {
                    await _settings.ClearValueAsync(context.Server.BaseUri, SubtitleDelayKey, SettingScopes.ProfileDevice, cancellationToken);
                }
                catch (SiloApiException ex) when (ex.Status == 404)
                {
                    // Nothing to clear is what this asked for.
                }
            }
        });
    }

    public IReadOnlyList<SubtitleInventoryItem> SubtitleTracks => _plan?.Subtitle.Inventory ?? [];

    public string? SubtitleTrack => _tracks.Subtitle?.Id is { Length: > 0 } id ? id : null;

    public void SelectSubtitle(string? trackId)
    {
        if (!CanChangeTracks)
        {
            return;
        }
        // A pick made while an added subtitle waits for its plan is the newer wish.
        _addedSubtitle = null;
        if (trackId == RequestedSubtitle)
        {
            return;
        }
        var plan = _plan!;
        var item = trackId is null ? null : plan.Subtitle.Inventory.FirstOrDefault(i => i.TrackId == trackId);
        if (trackId is not null && item is null)
        {
            return;
        }
        SaveSubtitleChoice(item);
        if (plan.EffectiveMediaFileId.Length == 0 || plan.EffectiveMediaFileId == _context!.Version.FileId)
        {
            // A new attempt after this starts with the pick instead of the automatic choice.
            _context = _context! with { SubtitlePick = item?.CombinedIndex ?? -1 };
        }
        SwitchSubtitle(trackId);
    }

    public void ShowAddedSubtitle(string storedSubtitleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedSubtitleId);
        if (!CanChangeTracks)
        {
            return;
        }
        _addedSubtitle = storedSubtitleId;
        _addedSubtitleAsked = false;
        AskForAddedSubtitle();
    }

    // There is no refresh: a track_change for the tracks that play answers with a fresh plan, inventory included, as
    // the web player re-reads it; the stream reloads from where the viewer is. A replan already on its way may list
    // the new subtitle with its own plan; if it does not, the ask follows it.
    private void AskForAddedSubtitle()
    {
        if (_addedSubtitle is null || _addedSubtitleAsked || _recovering)
        {
            return;
        }
        _addedSubtitleAsked = true;
        Recover(RefreshSubtitlesAsync);
    }

    private async Task RefreshSubtitlesAsync()
    {
        string? asked = _addedSubtitle;
        ResetPlanChain();
        double position = SourcePosition;
        _logger.LogInformation("Asking for a plan that lists the added subtitle at {Position:F1} s", position);
        bool refused = false;
        var decision = await ReplanAsync(ReplanOperations.TrackChange, position, [], refused: refusal =>
        {
            refused = true;
            _logger.LogWarning("The server did not plan again ({Failure}); the added subtitle shows with a later plan", ApiFailureText.Describe(refusal));
        });
        if (decision is { IsPlayable: true })
        {
            await AdoptAsync(decision, newSession: false, restorePosition: position);
        }
        else if (decision is not null)
        {
            _logger.LogWarning("The server has no stream for the tracks that play ({Reason}); playing on", decision.Terminal?.Reason);
        }
        // Asked once: a plan that does not list it will not show it. One added meanwhile is asked for after this; one
        // the server would not plan for may still come with a later plan.
        if (!refused && !_stopping && _addedSubtitle is not null && _addedSubtitle == asked)
        {
            _logger.LogWarning("The plan does not list the added subtitle");
            _addedSubtitle = null;
        }
    }

    // A plan that lists the added subtitle shows it, as the viewer's pick: Android selects a new download at once.
    private void TakeAddedSubtitle(PlaybackPlan plan)
    {
        if (_addedSubtitle is not { } id || plan.Subtitle.Inventory.FirstOrDefault(i => IsStored(i, id)) is not { } item)
        {
            return;
        }
        _addedSubtitle = null;
        SelectSubtitle(item.TrackId);
    }

    private static bool IsStored(SubtitleInventoryItem item, string storedId)
    {
        if (item.SyncKey is { Length: > 0 } key)
        {
            return key == StoredSyncKeyPrefix + storedId;
        }
        return item.Source == SubtitleSources.Downloaded && item.Url is { Length: > 0 } url
            && Uri.TryCreate(new Uri("http://player.invalid/"), url, out var uri)
            && System.Web.HttpUtility.ParseQueryString(uri.Query)[StoredIdParameter] == storedId;
    }

    // What the viewer asked for last: a pick still on its way, else the track that shows.
    private string? RequestedSubtitle =>
        _pendingSubtitle is { } pending && pending.FileId == _plan?.EffectiveMediaFileId ? pending.TrackId : SubtitleTrack;

    private void SwitchSubtitle(string? trackId)
    {
        var plan = _plan!;
        _pendingSubtitle = (plan.EffectiveMediaFileId, trackId);
        if (_recovering || !_started)
        {
            return;
        }
        var item = trackId is null ? null : plan.Subtitle.Inventory.FirstOrDefault(i => i.TrackId == trackId);
        if (trackId is not null && item is null)
        {
            // Not in this plan's inventory: a pick for a stream that no longer plays.
            _pendingSubtitle = null;
            return;
        }
        var tracks = _tracks with { Subtitle = item is null ? null : new TrackIdentity(item.TrackId, item.CombinedIndex) };
        if (CanShowLocally(plan, item))
        {
            _pendingSubtitle = null;
            _tracks = tracks;
            TracksChanged?.Invoke(this, EventArgs.Empty);
            _ = ShowSubtitlesAsync(_loadId);
            return;
        }
        var requested = _pendingSubtitle;
        Recover(() => ChangeTracksAsync(tracks, () =>
        {
            if (_pendingSubtitle == requested)
            {
                _pendingSubtitle = null;
            }
        }));
    }

    private void ApplyPendingSubtitle()
    {
        if (_pendingSubtitle is not { } pending)
        {
            return;
        }
        if (pending.FileId != _plan?.EffectiveMediaFileId || pending.TrackId == SubtitleTrack)
        {
            _pendingSubtitle = null;
            return;
        }
        SwitchSubtitle(pending.TrackId);
    }

    // Burned in, the subtitle is in the picture, and only a new plan takes it out. Otherwise off, any sidecar and an
    // embedded track of a direct play are the player's to show.
    private bool CanShowLocally(PlaybackPlan plan, SubtitleInventoryItem? item) =>
        plan.Subtitle.Mode != SubtitleModes.BurnIn
        && (item is null || EmbeddedStream(plan, item) is not null
            || (item.Delivery == SubtitleDeliveries.Sidecar && !string.IsNullOrWhiteSpace(item.Url)));

    // Shows the selected subtitle in load loadId; mpv was told at the load to show none. Subtitles are not playback:
    // whatever goes wrong here leaves the stream alone.
    private async Task ShowSubtitlesAsync(long loadId)
    {
        try
        {
            await ShowSubtitlesCoreAsync(loadId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Showing the subtitle failed");
        }
    }

    private async Task ShowSubtitlesCoreAsync(long loadId)
    {
        var plan = _plan!;
        var selected = SubtitleTrack is { } id ? plan.Subtitle.Inventory.FirstOrDefault(i => i.TrackId == id) : null;
        // A plan that shows none may still have one picked in the player since; a burn-in is in the picture already.
        if (selected is null || plan.Subtitle.Mode == SubtitleModes.BurnIn)
        {
            _engine.SetSubtitleStream(0);
            return;
        }
        if (EmbeddedStream(plan, selected) is { } ffmpeg && _engine.SubtitleStreamOf(ffmpeg) is { } inFile)
        {
            _subtitleBaseSeconds = 0;
            ApplySubtitleDelay();
            _engine.SetSubtitleStream(inFile);
            return;
        }
        if (!_loadedSubtitles.TryGetValue(selected.TrackId, out var read))
        {
            // The plan's own track comes as its artifact (a conversion under convert); any other as its sidecar.
            string? url = selected.TrackId == plan.Subtitle.TrackId && plan.Subtitle.Artifact is { Url.Length: > 0 } artifact
                ? artifact.Url
                : selected.Url;
            var server = _context!.Server;
            if (PlaybackUrl.ResolveSidecar(server.BaseUri, url) is not { } sidecar)
            {
                _logger.LogWarning("The subtitle {Track} has no file this app can fetch", selected.TrackId);
                ShowNoSubtitle(selected.TrackId);
                return;
            }
            var route = _proxy.Open(sidecar, MediaRouteScope.File, (refused, cancellationToken) => _auth.GetAsync(server, refused, cancellationToken));
            _subtitleRoutes.Add(route);
            read = _loadedSubtitles[selected.TrackId] = _engine.AddSubtitleAsync(route.LocalUrl, loadId);
        }
        int? added = await read;
        if (_stopping || loadId != _loadId || SubtitleTrack != selected.TrackId)
        {
            // Another load or another pick by now; that one shows its own.
            return;
        }
        if (!_loadedSubtitles.TryGetValue(selected.TrackId, out var current) || current != read)
        {
            // Its timing changed while it was read: the read after that one shows (ReloadSubtitleAsync).
            return;
        }
        if (added is not { } stream)
        {
            _logger.LogWarning("The subtitle {Track} could not be read", selected.TrackId);
            _loadedSubtitles.Remove(selected.TrackId);
            ShowNoSubtitle(selected.TrackId);
            return;
        }
        // Sidecar cues are source time; the player counts from the stream's own start (9.6).
        _subtitleBaseSeconds = -plan.Timeline.TimelineOffsetSeconds;
        ApplySubtitleDelay();
        _engine.SetSubtitleStream(stream);
    }

    // A track that cannot be shown is not left checked: the menu says none, a later pick of it tries again, and
    // replans no longer ask for it.
    private void ShowNoSubtitle(string trackId)
    {
        _engine.SetSubtitleStream(0);
        if (SubtitleTrack == trackId)
        {
            _tracks = _tracks with { Subtitle = null };
            TracksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // The FFmpeg stream index of an embedded track in a direct play: the plan names it for its own pick; another one's
    // is its place among the file's embedded tracks, which the inventory counts after the external ones.
    private int? EmbeddedStream(PlaybackPlan plan, SubtitleInventoryItem item)
    {
        if (plan.Delivery != PlaybackDeliveries.OriginalHttp || item.Source != SubtitleSources.Embedded)
        {
            return null;
        }
        if (item.TrackId == plan.Subtitle.TrackId && plan.Subtitle.Embedded is { } embedded)
        {
            return embedded.StreamIndex;
        }
        if (PlayingVersion is not { } version)
        {
            return null;
        }
        int ordinal = item.CombinedIndex - plan.Subtitle.Inventory.Count(i => i.Source == SubtitleSources.External);
        var tracks = version.SubtitleTracks.Where(t => !t.External).ToList();
        if (ordinal < 0 || ordinal >= tracks.Count)
        {
            return null;
        }
        var track = tracks[ordinal];
        // A list that does not line up with the inventory (a track the server left out shifts the rest) names another
        // track, often a forced or SDH one in the same language: the sidecar is the safe way then.
        bool sameLanguage = TrackLanguages.Key(track.Language) is not { } listed || TrackLanguages.Key(item.Language) is not { } inventory
            || listed == inventory;
        bool same = sameLanguage && track.Forced == item.Forced && track.HearingImpaired == item.HearingImpaired
            && SubtitleAutoSelection.CodecFamily(track.Codec) == SubtitleAutoSelection.CodecFamily(item.Codec);
        return same && track.Index > 0 ? track.Index : null;
    }

    private void SaveSubtitleChoice(SubtitleInventoryItem? item)
    {
        var choice = item is null
            ? SubtitleChoice.Off
            : SubtitleChoice.Of(item, item.HearingImpaired || SubtitleAutoSelection.LabelSaysHearingImpaired(item.Label));
        var context = _context!;
        string key = TrackPreferencesApi.KeyFor(context.Watch);
        SaveChoice("subtitle", cancellationToken => _preferences.SaveSubtitleAsync(context.Server.BaseUri, key, choice, cancellationToken));
    }

    private void CloseSubtitleRoutes()
    {
        foreach (var route in _subtitleRoutes)
        {
            route.Dispose();
        }
        _subtitleRoutes.Clear();
        _loadedSubtitles.Clear();
    }
}
