using System.Globalization;
using Granary.Core.Abstractions;
using Granary.Core.Api.Downloads;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// One playback of a download from this PC (docs/architecture.md, "Playing a download"): the media file goes straight into the
/// engine, and the title, tracks, chapters and marked parts come from the manifest kept beside it. As in Silo's apps
/// (Android's <c>PlayerViewModel.tryLocalPlayback</c>, Apple's <c>OfflinePlaybackContext</c>), it opens no playback session
/// on the server, online or not. While the server answers, it adds what only the server knows: the profile's settings, its
/// subtitle preference and where the profile left the title on another device.
/// </summary>
/// <remarks>
/// Used on the UI thread, as a <see cref="ServerPlayback"/> is: engine events are posted there. After the stop it leaves the
/// engine alone, which may already play the next title.
/// </remarks>
public sealed partial class LocalPlayback : IServerPlayback
{
    /// <summary>How long the start waits for the server's settings and the title's state; without them, the defaults.</summary>
    public static readonly TimeSpan PreferencesBudget = TimeSpan.FromSeconds(3);

    /// <summary>How long the stop waits for the server to take the last position; a slower send goes on by itself.</summary>
    public static readonly TimeSpan StopSendBudget = TimeSpan.FromSeconds(3);

    /// <summary>What the player says, beside Retry, when the file on this PC will not play.</summary>
    public const string CannotPlay = "This download can't be played. Try again, or delete it and download it again.";

    // An end this close to the title's length is its end; an earlier one is a file that breaks off.
    private const double EndTolerance = 30;

    private readonly StoredDownload _download;
    private readonly OfflineManifest _manifest;

    // The names of the files in the download's folder as the play began: a subtitle file that never arrived is not offered.
    private readonly IReadOnlySet<string> _files;
    private readonly IPlaybackEngine _engine;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISessionManager _session;
    private readonly PlaybackPreferencesReader _preferenceReader;
    private readonly WatchApi _watch;
    private readonly SettingsApi _settings;
    private readonly IPlaybackDevicePreferences _device;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalPlayback> _logger;

    // The server as the title started, for what the viewer changes while it plays; null when none was signed in.
    private Uri? _server;

    // The engine load that is this playback's; 0 before it and once it ended.
    private long _loadId;
    private long _engineSequence;
    private bool _started;
    private bool _stopping;
    private double _position;
    private bool _paused;
    private double? _pendingSeek;
    private readonly IDisposable? _playingMark;

    public LocalPlayback(
        LocalMedia media,
        IPlaybackEngine engine,
        IUiDispatcher dispatcher,
        ISessionManager session,
        PlaybackPreferencesReader preferences,
        WatchApi watch,
        SettingsApi settings,
        IPlaybackDevicePreferences device,
        TimeProvider time,
        ILogger<LocalPlayback> logger,
        AudioDelay? audioDelay = null,
        DownloadStore? store = null,
        OfflineProgressQueue? progress = null,
        OfflineProgressSync? sync = null,
        PlayingDownloads? playing = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Download.MediaPath is null)
        {
            throw new ArgumentException("A download plays once its media file is whole.", nameof(media));
        }
        _download = media.Download;
        _manifest = media.Manifest;
        _files = media.Files;
        _engine = engine;
        _dispatcher = dispatcher;
        _session = session;
        _preferenceReader = preferences;
        _watch = watch;
        _settings = settings;
        _device = device;
        _time = time;
        _logger = logger;
        _audioDelay = audioDelay;
        _store = store;
        _clearsSeen = store?.ClearGeneration ?? 0;
        _progress = progress;
        _sync = sync;
        (Title, Subtitle) = Titles();
        MediaInfo = Info();
        _engine.StateChanged += OnEngineStateChanged;
        _engine.Ended += OnEngineEnded;
        // From now until it is let go, whatever deletes downloads by itself leaves this one alone.
        _playingMark = playing?.Enter(_download.Record.Scope, _download.Record.DownloadId);
    }

    public event EventHandler<ServerPlaybackStatus>? StatusChanged;

#pragma warning disable CS0067 // No server session: nobody sends this player a message, replaces its stream or previews its seek bar.
    public event EventHandler<PlaybackNotice>? NoticeRaised;

    public event EventHandler? MarkersChanged;

    public event EventHandler? TrickplayChanged;

    public event EventHandler? PlanChangeChanged;

    public event EventHandler<string>? SubtitleTimingChanged;

    public event EventHandler<string>? SubtitleCuesReloaded;
#pragma warning restore CS0067

    public ServerPlaybackStatus Status { get; private set; } = new(ServerPlaybackPhase.Preparing);

    public string Title { get; }

    public string Subtitle { get; }

    public PlaybackMediaInfo MediaInfo { get; }

    /// <summary>The file is the title: its time is the title's own.</summary>
    public double TimelineOffsetSeconds => 0;

    public double? DurationSeconds => _manifest.DurationSeconds is > 0 and var seconds && double.IsFinite(seconds) ? seconds : null;

    public PlayingEpisode? Episode => IsEpisode && SeriesId is { Length: > 0 } series && SeasonNumber is { } season && EpisodeNumber is { } episode
        ? new PlayingEpisode(_download.Record.MediaItemId, series, _manifest.SeriesTitle ?? _download.Record.SeriesTitle ?? "", season, episode)
        : null;

    /// <summary>None: nothing of a local play is the server's.</summary>
    public string? SessionId => null;

    public PlaybackStreamInfo? StreamInfo => null;

    /// <summary>What the manifest says of the file, with the audio track that plays now.</summary>
    public PlaybackLocalFile? LocalFile
    {
        get
        {
            var audio = AudioTrack is { } ordinal && ordinal >= 0 && ordinal < _manifest.AudioTracks.Count ? _manifest.AudioTracks[ordinal] : null;
            return new PlaybackLocalFile(_manifest.Container, _manifest.FileSize > 0 ? _manifest.FileSize : _download.Record.ExpectedBytes,
                _manifest.CodecVideo, _manifest.Hdr, audio?.Codec ?? _manifest.CodecAudio, audio?.Channels ?? 0);
        }
    }

    public PlayerTrickplay? Trickplay => null;

    public PlanChange PlanChange => PlanChange.None;

    // What the title is, from the manifest and, where an older one leaves it out, the download's own record.
    private bool IsEpisode => _manifest.Type == WatchItemTypes.Episode || (_manifest.Type is null && _download.Record.SeriesId is not null);

    private string? SeriesId => _download.Record.SeriesId ?? _manifest.SeriesId;

    private int? SeasonNumber => _manifest.SeasonNumber ?? _download.Record.SeasonNumber;

    private int? EpisodeNumber => _manifest.EpisodeNumber ?? _download.Record.EpisodeNumber;

    private string OwnTitle => _manifest.Title is { Length: > 0 } title ? title : _download.Record.Title;

    public async Task StartAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        try
        {
            await PrepareAndLoadAsync(request);
        }
        catch (Exception ex)
        {
            // Only the kind: a file error's message holds the folder, with the Windows user's name.
            _logger.LogError("Playing download {DownloadId} failed unexpectedly ({Failure})", _download.Record.DownloadId, DownloadLog.Failure(ex));
            Fail();
        }
    }

    private async Task PrepareAndLoadAsync(PlayRequest request)
    {
        // Before the settings are read: a save of the audio delay done while they are is newer than what they say.
        long audioDelayRead = _audioDelay?.StartRead() ?? 0;
        _server = _session.ActiveServer?.BaseUri;
        var (preferences, detail) = await ReadFromServerAsync();
        if (_stopping)
        {
            return;
        }
        Preferences = preferences;
        _engine.SetSubtitleLook(preferences.Subtitles);
        _subtitleDelayMs = preferences.SubtitleDelayMs;
        _engine.SetSubtitleDelay(_subtitleDelayMs / 1000.0);
        _speed = preferences.Speed;
        _engine.SetSpeed(_speed);
        StartAudioDelay(preferences, audioDelayRead);
        TakeStartTracks(request, detail);
        SetStatus(Status);
        TracksChanged?.Invoke(this, EventArgs.Empty);
        double start = StartSeconds(request, detail);
        _position = start;
        _logger.LogInformation("Playing download {DownloadId} from this PC at {Start:F1} s (audio track {Audio})", _download.Record.DownloadId, start,
            AudioTrack?.ToString(CultureInfo.InvariantCulture) ?? "as the file has it");
        // Subtitles are this playback's to choose; left to itself, mpv would show one the file marks as default.
        _loadId = _engine.Load(MediaSource.Local(_download.MediaPath!, TimeSpan.FromSeconds(start)) with
        {
            AudioStream = AudioTrack is { } audio ? audio + 1 : null,
            SubtitleStream = 0,
        });
        // The title plays, however the one before was left: mpv keeps a pause across files.
        _engine.SetPaused(false);
    }

    // The profile's settings and the title as the server has it, each within the budget, or neither: a download plays without.
    private async Task<(PlaybackPreferences Preferences, WatchDetail? Detail)> ReadFromServerAsync()
    {
        if (_server is not { } server)
        {
            return (PlaybackPreferences.Default, null);
        }
        using var budget = new CancellationTokenSource(PreferencesBudget, _time);
        var preferences = ReadPreferencesAsync(server, budget.Token);
        var detail = ReadDetailAsync(server, budget.Token);
        return (await preferences, await detail);
    }

    private async Task<PlaybackPreferences> ReadPreferencesAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            return await _preferenceReader.ReadAsync(server, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("The playback settings did not come in time ({Failure}); the download plays with the defaults", ApiFailureText.Describe(ex));
            return PlaybackPreferences.Default;
        }
    }

    private async Task<WatchDetail?> ReadDetailAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            return await _watch.GetWatchAsync(server, _download.Record.MediaItemId, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ArgumentException || ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("The title's state on the server did not come ({Failure}); the download plays with what this PC knows",
                ApiFailureText.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Where the title starts: 0 to start over; else where the profile left it, rewound by this PC's Rewind on resume
    /// (<see cref="ResumeRewind"/>) unless the request suppresses it, as a stream starts. The point is the server's while it
    /// answers; this PC's own when it does not, or when this PC recorded one the server has not answered for yet
    /// (<see cref="HasUnsentProgress"/>): that one is the newer. One at the title's very end starts over (the Apple app's
    /// <c>resolvedResumePosition</c>).
    /// </summary>
    private double StartSeconds(PlayRequest request, WatchDetail? detail)
    {
        if (request.FromBeginning)
        {
            return 0;
        }
        double local = (_download.Record.ResumeMs ?? 0) / 1000.0;
        double position = detail is not null && !HasUnsentProgress ? detail.UserData?.PositionSeconds ?? 0 : local;
        if (position <= 0 || !double.IsFinite(position) || (DurationSeconds is { } duration && position >= duration - EndMargin))
        {
            return 0;
        }
        return request.SuppressesResumeRewind ? position : ResumeRewind.Apply(position, _device.ResumeRewindSeconds);
    }

    // A resume point within this of the end is the end.
    private const double EndMargin = 5;

    public bool CanReachInPlace(double sourceSeconds) => _started && !_stopping;

    public void Seek(double sourceSeconds)
    {
        if (_stopping || Status.Phase != ServerPlaybackPhase.Playing)
        {
            if (!_stopping && Status.Phase == ServerPlaybackPhase.Preparing)
            {
                // The file has not started: the seek waits for it.
                _pendingSeek = sourceSeconds;
            }
            return;
        }
        // The target is where the viewer is from now on, also before the engine gets there.
        _position = Math.Max(0, sourceSeconds);
        _engine.Seek(TimeSpan.FromSeconds(_position));
    }

    public Task StopAsync()
    {
        if (_stopping)
        {
            return Task.CompletedTask;
        }
        // Read now: the engine stops right after this call, and its idle state would read as position 0.
        RecordProgress(isFinal: true);
        _stopping = true;
        StopProgress();
        StopAudioDelay();
        SaveSettingsNow();
        return EndStopAsync();
    }

    // Closing the app waits for the stop: the last position is in the queue's file and the download's record by then. While
    // the server answers, it has the position by then too, as after a stream's stop: the pages that reload when a playback
    // stops (ViewerStateChanges) read where the viewer left off, not where they were before. Without the server the send
    // fails at once and the position waits; a send slower than the budget goes on by itself, and tells the pages when it
    // is through (OfflineProgressSync).
    private async Task EndStopAsync()
    {
        await Task.WhenAll(_saves, _records, _progress?.LatestSave ?? Task.CompletedTask).ConfigureAwait(false);
        if (_sync is not { } sync || !_played)
        {
            return;
        }
        using var budget = new CancellationTokenSource(StopSendBudget, _time);
        try
        {
            await sync.FlushAsync(readBack: true, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("The last position of download {DownloadId} was not sent within {Seconds} s; the send goes on by itself",
                _download.Record.DownloadId, StopSendBudget.TotalSeconds);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Also one let go without a stop: nothing of it acts on the engine or records a position from here on.
        _stopping = true;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Ended -= OnEngineEnded;
        StopAudioDelay();
        StopProgress();
        _speedSave?.Dispose();
        _subtitleDelaySave?.Dispose();
        _playingMark?.Dispose();
        await _saves.ConfigureAwait(false);
        await _records.ConfigureAwait(false);
    }

    private void OnEngineStateChanged(object? sender, PlaybackSnapshot state) => _dispatcher.Post(() => ShowEngineState(state));

    private void ShowEngineState(PlaybackSnapshot state)
    {
        // The engine raises from more than one thread: one made before a newer one can arrive after it.
        if (state.Sequence < _engineSequence)
        {
            return;
        }
        _engineSequence = state.Sequence;
        if (_stopping || _loadId == 0 || state.LoadId != _loadId)
        {
            return;
        }
        if (state is not { IsLoaded: true, HasStarted: true })
        {
            return;
        }
        _position = state.Position.TotalSeconds;
        _paused = state.IsPaused;
        _engineDuration = state.Duration?.TotalSeconds;
        if (_started)
        {
            return;
        }
        _started = true;
        _played = true;
        StartProgress();
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        if (_pendingSeek is { } target)
        {
            _pendingSeek = null;
            Seek(target);
        }
        ShowStartSubtitle();
    }

    private void OnEngineEnded(object? sender, PlaybackEnded ended) => _dispatcher.Post(() =>
    {
        if (_stopping || _loadId == 0 || ended.LoadId != _loadId || ended.Reason == PlaybackEndReason.Stopped)
        {
            return;
        }
        _loadId = 0;
        if (ended.Reason != PlaybackEndReason.EndOfFile)
        {
            _logger.LogWarning("Download {DownloadId} did not play", _download.Record.DownloadId);
            Fail();
            return;
        }
        // The file's own length as the player read it, else the manifest's. Without either, an end can only be taken for the end.
        double? length = _engineDuration ?? DurationSeconds;
        if (length is { } whole && _position < whole - EndTolerance)
        {
            // The file breaks off long before its length: a damaged copy, not the title's end. As Silo's Apple app, the
            // title is not watched and nothing plays next ("The downloaded video file ends early.").
            _logger.LogWarning("Download {DownloadId} ended {Seconds:F0} s before its length", _download.Record.DownloadId, whole - _position);
            Fail();
            return;
        }
        // Played to its end: that is where the viewer is, whatever the last position read said.
        _position = length ?? _position;
        RecordProgress(isFinal: true, playedToTheEnd: true);
        StopProgress();
        _started = false;
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
    });

    // The file on this PC will not play. Nothing streams in its place: as in Silo's apps, the viewer hears it, and Retry
    // tries the file again.
    private void Fail()
    {
        if (_stopping)
        {
            return;
        }
        // Where the viewer got to before the file broke off is kept.
        RecordProgress(isFinal: true);
        StopProgress();
        _loadId = 0;
        _started = false;
        _engine.Stop();
        SetStatus(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, CannotPlay, CanRetry: true));
    }

    private void SetStatus(ServerPlaybackStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    // The player's two lines, as a stream's (PlaybackStarter.Titles).
    private (string Title, string Subtitle) Titles() => IsEpisode
        ? PlaybackStarter.EpisodeTitles(OwnTitle, _manifest.SeriesTitle ?? _download.Record.SeriesTitle, SeasonNumber, EpisodeNumber)
        : (OwnTitle, _manifest.Year?.ToString(CultureInfo.InvariantCulture) ?? "");

    // Windows' media controls and the Discord status, as a stream fills them. The picture is the one kept with the download
    // (the Apple app's posterFileURL): the film's poster or the episode's still, else the backdrop.
    private PlaybackMediaInfo Info()
    {
        var artwork = ArtworkFile("poster") ?? ArtworkFile("backdrop");
        return IsEpisode
            ? PlaybackMediaInfo.OfEpisode(_manifest.Title ?? _download.Record.Title, _manifest.SeriesTitle ?? _download.Record.SeriesTitle,
                SeasonNumber, EpisodeNumber, _manifest.Year, SeriesId, artwork)
            : PlaybackMediaInfo.OfTitle(OwnTitle, _manifest.Year, isMovie: true, _download.Record.MediaItemId, artwork);
    }

    private Uri? ArtworkFile(string kind) =>
        _download.Record.ArtworkFiles.TryGetValue(kind, out string? name) ? new Uri(Path.Combine(_download.Folder, name)) : null;
}
