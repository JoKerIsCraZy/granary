using System.Globalization;
using Granary.Core.Api.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// The tracks that play and the viewer's picks (docs/api-contract.md, 8.8), as Silo's Apple and Android clients
// handle them: a direct play switches audio on the spot, any other stream is replanned with track_change, and every
// pick is saved for the series.
public sealed partial class ServerPlayback
{
    // A save that hangs must not hold the stop, nor the dispose after it, for the HTTP timeout.
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(10);

    private Task _trackSaves = Task.CompletedTask;

    // The viewer's latest pick until a stream plays it: one made while the stream is being replaced, or the one a
    // track_change replan is fetching. It names its file, so an edition fallback in between drops it.
    private (string FileId, int Ordinal)? _pendingAudio;

    public event EventHandler? TracksChanged;

    public IReadOnlyList<WatchAudioTrack> AudioTracks => PlayingVersion?.AudioTracks ?? [];

    public int? AudioTrack => _tracks.Audio is { } audio ? Ordinal(audio) : null;

    // The file the plan plays: the chosen one, or after an edition fallback another file of the title.
    private WatchFileVersion? PlayingVersion => _plan is { } plan && _context is { } context
        ? plan.EffectiveMediaFileId.Length == 0
            ? context.Version
            : context.Watch.Versions.FirstOrDefault(v => v.FileId == plan.EffectiveMediaFileId)
        : null;

    public void SelectAudio(int ordinal)
    {
        if (!CanChangeTracks || PlayingVersion is not { } version || ordinal < 0 || ordinal >= version.AudioTracks.Count
            || ordinal == RequestedAudio(version))
        {
            return;
        }
        SaveAudioChoice(version, ordinal);
        if (version.FileId == _context!.Version.FileId)
        {
            // A new attempt after this (a server restart, a lost session) starts with the pick too.
            _context = _context with { Version = _context.Version with { EffectiveAudioTrackIndex = ordinal } };
        }
        SwitchAudio(version, ordinal);
    }

    private bool CanChangeTracks => _plan is not null && !_stopping && Status.Phase is not (ServerPlaybackPhase.Failed or ServerPlaybackPhase.Ended);

    // What the viewer asked for last: a pick still on its way, else the track that plays.
    private int? RequestedAudio(WatchFileVersion version) =>
        _pendingAudio is { } pending && pending.FileId == version.FileId ? pending.Ordinal : AudioTrack;

    private void SwitchAudio(WatchFileVersion version, int ordinal)
    {
        _pendingAudio = (version.FileId, ordinal);
        if (_recovering || !_started)
        {
            return;
        }
        var tracks = _tracks with { Audio = new TrackIdentity($"file:{version.FileId}:audio:{ordinal}", ordinal) };
        if (AudioStream(_plan!, tracks) is { } stream && _engine.SetAudioStream(stream))
        {
            // A direct play is the file with every track in it.
            _logger.LogInformation("Switched to audio stream {Stream} in the player", stream);
            _pendingAudio = null;
            _tracks = tracks;
            TracksChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var requested = _pendingAudio;
        Recover(() => ChangeTracksAsync(tracks, () =>
        {
            if (_pendingAudio == requested)
            {
                _pendingAudio = null;
            }
        }));
    }

    // Once a stream plays: picks made meanwhile apply to it, unless it plays them already or they are another file's.
    private void ApplyPendingTracks()
    {
        ApplyPendingAudio();
        ApplyPendingSubtitle();
    }

    private void ApplyPendingAudio()
    {
        if (_pendingAudio is not { } pending)
        {
            return;
        }
        if (PlayingVersion is not { } version || version.FileId != pending.FileId || pending.Ordinal >= version.AudioTracks.Count
            || pending.Ordinal == AudioTrack)
        {
            _pendingAudio = null;
            return;
        }
        SwitchAudio(version, pending.Ordinal);
    }

    // A track the stream does not carry: the session plans one that does, from where the viewer is (8.7). The stream
    // that plays is fine, so a refusal leaves it playing rather than failing playback. Answered, the pick that asked is
    // done with; one made during the replan stays for the next stream.
    private async Task ChangeTracksAsync(SelectedTracks tracks, Action answered)
    {
        ResetPlanChain();
        double position = SourcePosition;
        _logger.LogInformation("Asking for a stream with the chosen tracks at {Position:F1} s", position);
        var decision = await ReplanAsync(ReplanOperations.TrackChange, position, [], tracks: tracks,
            refused: refusal => _logger.LogWarning("The server did not change the tracks ({Failure}); playing on", ApiFailureText.Describe(refusal)));
        answered();
        if (decision is null)
        {
            return;
        }
        if (!decision.IsPlayable)
        {
            _logger.LogWarning("The server has no stream with the chosen tracks ({Reason}); playing on", decision.Terminal?.Reason);
            return;
        }
        await AdoptAsync(decision, newSession: false, restorePosition: position);
    }

    private void SaveAudioChoice(WatchFileVersion version, int ordinal)
    {
        var context = _context!;
        string key = TrackPreferencesApi.KeyFor(context.Watch);
        var preference = AudioPreference.Of(version.AudioTracks[ordinal], ordinal);
        SaveChoice("audio", cancellationToken => _preferences.SaveAudioAsync(context.Server.BaseUri, key, preference, cancellationToken));
    }

    // Saved in the order the viewer picked, whatever happens to playback meanwhile. Only the viewer's own picks are
    // saved, never what the server or a fallback chose: that would turn an automatic outcome into a preference.
    private void SaveChoice(string kind, Func<CancellationToken, Task> save)
    {
        var previous = _trackSaves;
        _trackSaves = SaveAsync();

        async Task SaveAsync()
        {
            await previous;
            using var timeout = new CancellationTokenSource(SaveTimeout, _time);
            try
            {
                await save(timeout.Token);
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                _logger.LogWarning("Saving the {Kind} choice failed: {Failure}", kind, ApiFailureText.Describe(ex));
            }
            catch (Exception ex)
            {
                // A preference, not playback: whatever went wrong, the title plays on and the stop goes out.
                _logger.LogError(ex, "Saving the {Kind} choice failed unexpectedly", kind);
            }
        }
    }

    // The tracks of a plan; a direct play then plays the selected audio stream, not the file's default. A direct play
    // of the same file that names no audio keeps the one that played: the player chooses it there, not the server. A
    // plan whose subtitle mode is off shows none, whatever it lists.
    private void TakeTracks(PlaybackPlan plan)
    {
        var tracks = plan.SelectedTracks;
        if (plan.Subtitle.Mode == SubtitleModes.Off)
        {
            tracks = tracks with { Subtitle = null };
        }
        if (tracks.Audio is null && plan.Delivery == PlaybackDeliveries.OriginalHttp && _tracks.Audio is { } playing
            && playing.Id.StartsWith($"file:{plan.EffectiveMediaFileId}:audio:", StringComparison.Ordinal))
        {
            tracks = tracks with { Audio = playing };
        }
        _tracks = tracks;
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    // The ordinal of file:{id}:audio:{n}, from the index or else from the id.
    private static int? Ordinal(TrackIdentity track)
    {
        int? ordinal = track.Index ?? (int.TryParse(track.Id.AsSpan(track.Id.LastIndexOf(':') + 1), NumberStyles.None,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : null);
        return ordinal is >= 0 ? ordinal : null;
    }
}
