using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>A marked part of the playing file (<see cref="WatchMarkerKinds"/>), <c>[start, end)</c> in source seconds.</summary>
public sealed record PlaybackMarker(string Kind, double StartSeconds, double EndSeconds)
{
    public bool Contains(double seconds) => seconds >= StartSeconds && seconds < EndSeconds;

    /// <summary>The same part, perhaps as the server measured it again: same kind, overlapping ranges.</summary>
    public bool IsSamePart(PlaybackMarker other) =>
        other.Kind == Kind && other.StartSeconds < EndSeconds && StartSeconds < other.EndSeconds;

    /// <summary>
    /// The parts of <paramref name="version"/> the player acts on (docs/api-contract.md, 11.1): the intro from its
    /// single projection, as Android takes it, so a second intro segment is never offered; every recap, credits and
    /// preview segment, or a kind's projection when the server lists no segment of it.
    /// </summary>
    public static IReadOnlyList<PlaybackMarker> Of(WatchFileVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var markers = new List<PlaybackMarker>();
        var intro = version.Intro is { } projection
            ? new PlaybackMarker(WatchMarkerKinds.Intro, projection.StartSeconds, projection.EndSeconds)
            : version.MarkerSegments.Where(s => s.Kind == WatchMarkerKinds.Intro)
                .Select(s => new PlaybackMarker(s.Kind, s.StartSeconds, s.EndSeconds)).FirstOrDefault();
        if (intro is not null)
        {
            markers.Add(intro);
        }
        foreach (var (kind, single) in new[]
        {
            (WatchMarkerKinds.Recap, version.Recap),
            (WatchMarkerKinds.Credits, version.Credits),
            (WatchMarkerKinds.Preview, version.Preview),
        })
        {
            var segments = version.MarkerSegments.Where(s => s.Kind == kind).ToList();
            if (segments.Count > 0)
            {
                markers.AddRange(segments.Select(s => new PlaybackMarker(kind, s.StartSeconds, s.EndSeconds)));
            }
            else if (single is not null)
            {
                markers.Add(new PlaybackMarker(kind, single.StartSeconds, single.EndSeconds));
            }
        }
        return [.. markers.Where(m => m.EndSeconds > m.StartSeconds).OrderBy(m => m.StartSeconds)];
    }
}

/// <summary>The profile's settings for marked parts and what follows the title (docs/api-contract.md, 10.7 and 11.2).</summary>
/// <param name="IntroSkipMode"><c>never</c>, <c>ask</c> or <c>always</c>.</param>
/// <param name="NextUpPromptSeconds">Without a credits marker, the next episode is offered this long before the end; 0 only at the end.</param>
public sealed record MarkerSettings(string IntroSkipMode, bool AutoSkipCredits, bool AutoSkipRecap, bool AutoPlayNext, int NextUpPromptSeconds)
{
    public const string Never = "never";
    public const string Ask = "ask";
    public const string Always = "always";

    public static MarkerSettings Default { get; } = new(Ask, false, false, true, 30);
}

/// <summary>The pill the player shows over the picture: its label, a confirmation above it, and how far its time has run.</summary>
/// <param name="Progress">0 to 1 for an intro prompt, which goes away after 5 s; null for a pill that stays.</param>
public sealed record SkipPrompt(string Label, string? Caption, double? Progress);

/// <summary>What the player is doing, as the intro prompt's clock sees it.</summary>
public enum MarkerActivity
{
    Playing,

    /// <summary>Loading or buffering: shorter than 1.5 s it counts as playing, longer as a pause.</summary>
    Stalled,

    Paused,
}

/// <summary>
/// What the player offers inside a marked part, as Silo's design for intro skipping lays it down
/// (docs/api-contract.md, 11.2) and its web player shows it:
/// <list type="bullet">
/// <item><c>ask</c>: entering the intro shows "Skip Intro" for 5 s of playing time; skipping seeks to its end and
/// resolves it, the time running out only hides it, so seeking back in offers it again.</item>
/// <item><c>always</c>: entering the intro skips it at once and offers "Watch Intro" for 5 s, which seeks back to
/// its start; either way it is resolved.</item>
/// <item>A resolved intro is never offered again. Recap, credits and preview get a pill that stays while inside
/// them ("Skip Credits"), and skip themselves once where the profile says so, as Apple's player does.</item>
/// </list>
/// The clock starts once playback runs; a pause stops it, a stall only after 1.5 s. A part counts as the one before when
/// the server measures it again (a later read of the markers). Positions are the title's own seconds.
/// </summary>
public sealed class MarkerPrompts
{
    private static readonly TimeSpan PromptTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallGrace = TimeSpan.FromSeconds(1.5);

    private readonly HashSet<PlaybackMarker> _resolved = [];
    private readonly HashSet<PlaybackMarker> _skippedByThemselves = [];
    private IReadOnlyList<PlaybackMarker> _markers = [];
    private MarkerSettings _settings = MarkerSettings.Default;

    // The intro prompt that shows: its intro, whether it is the undo of an automatic skip, and how long it has shown.
    private PlaybackMarker? _intro;
    private bool _undo;
    private TimeSpan _shown;
    private bool _clockStarted;
    private TimeSpan? _lastTick;

    // What the player did since the last tick: it reports changes, not a pause going on, so the time up to a tick is
    // the time of the activity the tick before reported.
    private MarkerActivity _lastActivity = MarkerActivity.Paused;
    private TimeSpan? _stalledSince;

    // An ask prompt whose time ran out stays away until the viewer leaves the intro.
    private PlaybackMarker? _timedOut;
    private PlaybackMarker? _part;

    public SkipPrompt? Prompt { get; private set; }

    public IReadOnlyList<PlaybackMarker> Markers => _markers;

    /// <summary>
    /// Takes the parts of the file that plays now; what was resolved stays resolved, and a prompt whose part is still
    /// there goes on as it was.
    /// </summary>
    public void Reset(IReadOnlyList<PlaybackMarker> markers, MarkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(settings);
        _markers = markers;
        _settings = settings;
        _intro = _intro is { } intro ? markers.FirstOrDefault(intro.IsSamePart) : null;
        _timedOut = _timedOut is { } timedOut ? markers.FirstOrDefault(timedOut.IsSamePart) : null;
        _part = null;
        ShowPrompt();
    }

    /// <summary>Playback cannot go on for now (a reconnect, a new plan): the prompt's clock stops until the next update.</summary>
    public void Suspend(TimeSpan now) => Tick(MarkerActivity.Paused, now);

    /// <summary>Follows the position; answers where to seek when a part skips itself.</summary>
    public double? Update(double position, MarkerActivity activity, TimeSpan now)
    {
        Tick(activity, now);

        var intro = _settings.IntroSkipMode is MarkerSettings.Ask or MarkerSettings.Always
            ? _markers.FirstOrDefault(m => m.Kind == WatchMarkerKinds.Intro && m.Contains(position))
            : null;
        if (_intro is { } shown)
        {
            if (_shown >= PromptTime)
            {
                // The undo's time out settles it; an ask prompt only goes away.
                if (_undo)
                {
                    _resolved.Add(shown);
                }
                else
                {
                    _timedOut = shown;
                }
                _intro = null;
            }
            else if (!_undo && intro != shown)
            {
                _intro = null;
            }
        }
        if (intro is null || intro != _timedOut)
        {
            _timedOut = null;
        }

        double? seek = null;
        if (_intro is null && intro is not null && !_resolved.Any(intro.IsSamePart) && _timedOut is null)
        {
            _intro = intro;
            _undo = _settings.IntroSkipMode == MarkerSettings.Always;
            _shown = TimeSpan.Zero;
            // A prompt that comes up while playback runs starts its clock at once; one during loading, once it runs.
            _clockStarted = activity == MarkerActivity.Playing;
            if (_undo)
            {
                seek = intro.EndSeconds;
            }
        }

        _part = null;
        if (_intro is null && _markers.FirstOrDefault(m => m.Kind != WatchMarkerKinds.Intro && m.Contains(position)) is { } part)
        {
            if (_skippedByThemselves.Any(part.IsSamePart))
            {
                // Passed once; back in it, the viewer means to watch it.
            }
            else if (SkipsItself(part))
            {
                _skippedByThemselves.Add(part);
                seek = part.EndSeconds;
            }
            else
            {
                _part = part;
            }
        }
        ShowPrompt();
        return seek;
    }

    /// <summary>The viewer pressed the pill: answers where to seek, or null when nothing shows.</summary>
    public double? Select()
    {
        if (_intro is { } intro)
        {
            _resolved.Add(intro);
            _intro = null;
            Prompt = null;
            return _undo ? intro.StartSeconds : intro.EndSeconds;
        }
        if (_part is { } part)
        {
            _part = null;
            Prompt = null;
            return part.EndSeconds;
        }
        return null;
    }

    /// <summary>The viewer turned the intro prompt away (Esc): it goes and is not offered again. False when none shows.</summary>
    public bool Dismiss()
    {
        if (_intro is not { } intro)
        {
            return false;
        }
        _resolved.Add(intro);
        _intro = null;
        Prompt = null;
        return true;
    }

    // The prompt's 5 s are playing time: from the first moment playback runs, through the first 1.5 s of a stall. The
    // time since the last tick is counted as what the player was doing then.
    private void Tick(MarkerActivity activity, TimeSpan now)
    {
        var last = _lastTick ?? now;
        var during = _lastActivity;
        _lastTick = now;
        _lastActivity = activity;
        if (_intro is not null && now > last)
        {
            if (during == MarkerActivity.Playing)
            {
                _clockStarted = true;
                _shown += now - last;
            }
            else if (during == MarkerActivity.Stalled && _clockStarted && _stalledSince is { } since && since + StallGrace > last)
            {
                var graceEnd = since + StallGrace;
                _shown += (now < graceEnd ? now : graceEnd) - last;
            }
        }
        _stalledSince = activity == MarkerActivity.Stalled ? _stalledSince ?? now : null;
    }

    private bool SkipsItself(PlaybackMarker part) => part.Kind switch
    {
        WatchMarkerKinds.Credits => _settings.AutoSkipCredits,
        WatchMarkerKinds.Recap => _settings.AutoSkipRecap,
        _ => false,
    };

    private void ShowPrompt()
    {
        if (_intro is not null)
        {
            double progress = Math.Clamp(_shown / PromptTime, 0, 1);
            Prompt = _undo ? new SkipPrompt("Watch Intro", "Intro skipped", progress) : new SkipPrompt("Skip Intro", null, progress);
            return;
        }
        Prompt = _part?.Kind switch
        {
            WatchMarkerKinds.Recap => new SkipPrompt("Skip Recap", null, null),
            WatchMarkerKinds.Credits => new SkipPrompt("Skip Credits", null, null),
            WatchMarkerKinds.Preview => new SkipPrompt("Skip Preview", null, null),
            _ => null,
        };
    }
}
