using System.ComponentModel;
using System.Globalization;
using Granary.Core.Abstractions;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The player on Discord's status (<see cref="IRichPresence"/>) while Settings → Playback → This PC allows it: "Watching"
/// the series, the film or the file, the episode or the year under it, the timeline, and the title's poster and page from
/// TMDB (<see cref="TmdbTitleLookup"/>). It goes as soon as nothing plays: the player left, a title loading, failed or
/// ended, or the setting turned off. The texts are English; Discord writes "Watching" itself, in the viewer's language.
/// </summary>
/// <remarks>
/// Discord takes about 5 updates in 20 seconds. As <see cref="PlayerSystemMedia"/> does for Windows' controls, only a change
/// of what shows goes out: the timeline runs on in Discord by itself, so playing on sends nothing, a jump of it does. A
/// change comes at most every <see cref="MinimumSpacing"/>; one that has to wait goes out as things are by then.
/// </remarks>
public sealed class PlayerRichPresence : IDisposable
{
    /// <summary>The least time between two updates, so no more than 5 go out in 20 seconds.</summary>
    public static readonly TimeSpan MinimumSpacing = TimeSpan.FromSeconds(4);

    // A timeline this far from the one shown is a jump, a seek or a stall, and shows.
    private const double JumpSeconds = 3;

    private const string PausedText = "Paused";

    // What the status is read from; the player's other changes leave it alone.
    private static readonly HashSet<string> Shown =
    [
        nameof(PlayerViewModel.Phase), nameof(PlayerViewModel.IsPaused), nameof(PlayerViewModel.HasLeft), nameof(PlayerViewModel.MediaInfo),
        nameof(PlayerViewModel.PositionSeconds), nameof(PlayerViewModel.DurationSeconds), nameof(PlayerViewModel.Speed),
    ];

    private readonly PlayerViewModel _player;
    private readonly IRichPresence _presence;
    private readonly IPlaybackDevicePreferences _preferences;
    private readonly TmdbTitleLookup _titles;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _closing = new();
    private RichPresenceState? _shown;
    private long? _sentAt;
    private ITimer? _held;
    private TitleKey? _lookingUp;
    private (TitleKey Key, TmdbTitle? Title)? _found;
    private bool _disposed;

    public PlayerRichPresence(PlayerViewModel player, IRichPresence presence, IPlaybackDevicePreferences preferences, TmdbTitleLookup titles,
        IUiDispatcher dispatcher, TimeProvider time)
    {
        _player = player;
        _presence = presence;
        _preferences = preferences;
        _titles = titles;
        _dispatcher = dispatcher;
        _time = time;
        _player.PropertyChanged += OnPlayerChanged;
        _preferences.DiscordStatusChanged += OnSettingChanged;
        Update();
    }

    /// <summary>The latest TMDB lookup, done once its answer is taken in.</summary>
    internal Task Lookup { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _player.PropertyChanged -= OnPlayerChanged;
        _preferences.DiscordStatusChanged -= OnSettingChanged;
        _closing.Cancel();
        _closing.Dispose();
        Clear();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || Shown.Contains(e.PropertyName))
        {
            Update();
        }
    }

    // Raised on the thread that changed the setting, which is the UI's.
    private void OnSettingChanged(object? sender, EventArgs e) => _dispatcher.Post(Update);

    private void Update()
    {
        if (_disposed)
        {
            return;
        }
        if (Read() is not { } state)
        {
            Clear();
        }
        else if (Differs(state))
        {
            Show(state);
        }
        else
        {
            // Back to what shows: a change that waited has nothing to send.
            StopWaiting();
        }
        // Also while the title loads, so its poster is there when it plays.
        LookUp();
    }

    // What shows now; null when nothing should.
    private RichPresenceState? Read()
    {
        var player = _player;
        if (!_preferences.DiscordStatus || player.HasLeft || player.Phase != PlayerPhase.Playing)
        {
            return null;
        }
        var info = player.MediaInfo;
        var (details, second) = Lines(info);
        if (RichPresenceText.Fit(details) is not { } first)
        {
            return null;
        }
        string? state = !player.IsPaused ? RichPresenceText.Fit(second)
            : RichPresenceText.Fit(second, " · " + PausedText) ?? PausedText;
        var (start, end) = player.IsPaused ? (null, null) : Timeline();
        var found = _found is { } known && known.Key == TitleKey.Of(info) ? known.Title : null;
        return new RichPresenceState(first, state, start, end, found?.Poster, found?.Page);
    }

    // The series over "S01E03 · Pilot", the film over its year, anything else by the title the player shows.
    private static (string Details, string? State) Lines(PlaybackMediaInfo info)
    {
        switch (info.Kind)
        {
            case PlaybackMediaKind.Movie:
                return (info.Title, info.Year?.ToString(CultureInfo.InvariantCulture));
            case PlaybackMediaKind.Episode:
                string? code = info.SeasonNumber is { } season && info.EpisodeNumber is { } episode
                    ? string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}")
                    : null;
                // Without its series, the episode's own title leads, and is not said twice.
                return info.SeriesTitle is { } series
                    ? (series, string.Join(" · ", new[] { code, info.EpisodeTitle }.Where(part => !string.IsNullOrWhiteSpace(part))))
                    : (info.Title, code);
            default:
                return (info.Title, null);
        }
    }

    // Discord draws the timeline from where it started and where it ends. At another speed it runs in real time, so it ends
    // when the title does. None for a length not known.
    private (DateTimeOffset? Start, DateTimeOffset? End) Timeline()
    {
        double duration = _player.DurationSeconds;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return (null, null);
        }
        double position = double.IsFinite(_player.PositionSeconds) ? Math.Clamp(_player.PositionSeconds, 0, duration) : 0;
        double speed = double.IsFinite(_player.Speed) && _player.Speed > 0 ? _player.Speed : 1;
        var start = _time.GetUtcNow() - TimeSpan.FromSeconds(position / speed);
        return (start, start + TimeSpan.FromSeconds(duration / speed));
    }

    // Something shown changed, or the timeline jumped; a timeline that only ran on, as Discord runs it too, did not.
    private bool Differs(RichPresenceState state)
    {
        if (_shown is not { } shown)
        {
            return true;
        }
        return shown with { StartedAt = state.StartedAt, EndsAt = state.EndsAt } != state
            || Jumped(shown.StartedAt, state.StartedAt) || Jumped(shown.EndsAt, state.EndsAt);
    }

    private static bool Jumped(DateTimeOffset? shown, DateTimeOffset? now) =>
        shown is { } before && now is { } after ? Math.Abs((after - before).TotalSeconds) > JumpSeconds : shown.HasValue != now.HasValue;

    private void Show(RichPresenceState state)
    {
        if (_sentAt is { } sentAt && _time.GetElapsedTime(sentAt) is var since && since < MinimumSpacing)
        {
            // Too soon for Discord: it goes out once it may, as things are by then.
            _held ??= _time.CreateTimer(static self => ((PlayerRichPresence)self!).OnWaitOver(), this, MinimumSpacing - since, Timeout.InfiniteTimeSpan);
            return;
        }
        StopWaiting();
        _shown = state;
        _sentAt = _time.GetTimestamp();
        _presence.Show(state);
    }

    // At once, whatever went out last: nothing plays, so nothing may show.
    private void Clear()
    {
        StopWaiting();
        if (_shown is null)
        {
            return;
        }
        _shown = null;
        _sentAt = _time.GetTimestamp();
        _presence.Clear();
    }

    // On a timer's thread.
    private void OnWaitOver() => _dispatcher.Post(() =>
    {
        StopWaiting();
        Update();
    });

    private void StopWaiting()
    {
        _held?.Dispose();
        _held = null;
    }

    // The title's TMDB page and poster, once per title and only while the setting is on.
    private void LookUp()
    {
        var info = _player.MediaInfo;
        if (!_preferences.DiscordStatus || _player.HasLeft || TitleKey.Of(info) is not { } key || _found?.Key == key || _lookingUp == key)
        {
            return;
        }
        _lookingUp = key;
        Lookup = FindAsync(key, _closing.Token);
    }

    private async Task FindAsync(TitleKey key, CancellationToken cancellationToken)
    {
        TmdbTitle? title;
        try
        {
            title = await _titles.FindAsync(key.Kind, key.CatalogId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The player page closed.
            return;
        }
        _dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            if (_lookingUp == key)
            {
                _lookingUp = null;
            }
            _found = (key, title);
            Update();
        });
    }

    // The TMDB entry a title stands for: a movie's own, an episode's series'.
    private readonly record struct TitleKey(PlaybackMediaKind Kind, string CatalogId)
    {
        public static TitleKey? Of(PlaybackMediaInfo info) =>
            info.Kind != PlaybackMediaKind.Other && info.CatalogId is { } id ? new TitleKey(info.Kind, id) : null;
    }
}
