namespace Granary.Core.Playback;

/// <summary>What the player shows of the next episode.</summary>
public enum NextUpView
{
    None,

    /// <summary>"Up Next in 31s" over the picture, bottom right, with Play Now and Cancel.</summary>
    Card,

    /// <summary>"Playing Next": the next episode across the page, the video small in a corner.</summary>
    Screen,
}

/// <summary>What a step of <see cref="NextUpFlow"/> asks of the player.</summary>
public enum NextUpStep
{
    None,

    /// <summary>The countdown ran out: play the next episode.</summary>
    PlayNext,

    /// <summary>A movie ended: nothing follows it here, so the player goes back.</summary>
    Leave,
}

/// <summary>
/// Counts the episodes that started by themselves since the viewer last did something. At the threshold the next one
/// waits for the viewer (Android's pass-out gate, <c>player.passout_threshold</c>, default 3).
/// </summary>
public sealed class AutoPlayStreak
{
    public const int Threshold = 3;

    public int Count { get; private set; }

    public bool IsGated => Count >= Threshold;

    public void RecordAutomatic() => Count++;

    public void Reset() => Count = 0;
}

/// <summary>
/// The way to the next episode, Silo's web player's look with the apps' rules (docs/api-contract.md, 10.7):
/// <list type="bullet">
/// <item>Crossing the credits' start going forward (without credits, the length less
/// <c>playback.next_up_prompt_seconds</c>; 0 only at the end) shows the card, counting the playback left when auto-play
/// is on. Resuming or seeking past the point does not.</item>
/// <item>Crossing 30 s before the end of an episode shows the screen, as the web player does; the video goes on small.</item>
/// <item>At the end the screen counts down 10 s and plays the next episode, when auto-play is on, the viewer has not
/// turned the prompt away (Cancel, Keep watching, a pause or a seek during the card), and fewer than three episodes
/// started by themselves in a row. A movie's end leaves the player.</item>
/// </list>
/// </summary>
public sealed class NextUpFlow
{
    public const int CountdownSeconds = 10;
    public const double ScreenSecondsBeforeEnd = 30;

    // Positions further apart than this come from a seek, not from playing on: a seek past a point does not cross it.
    private const double PlayingStep = 5;

    private bool _isEpisode;
    private MarkerSettings _settings = MarkerSettings.Default;
    private bool _gated;
    private double? _creditsStart;
    private double? _lastPosition;
    private double _position;
    private double _duration;
    private bool _cardArmed;
    private bool _screenArmed;
    private bool _dismissed;
    private bool _countdownStopped;
    private bool _ended;
    private TimeSpan? _countdownEnds;

    public NextEpisode? Next { get; private set; }

    public NextUpView View { get; private set; }

    public bool HasEnded => _ended;

    public bool AutoPlayNext => _settings.AutoPlayNext;

    /// <summary>What the card or the screen counts: the playback left before the end, the countdown at it; null for none.</summary>
    public int? Seconds { get; private set; }

    /// <summary>The countdown at the end runs.</summary>
    public bool IsCountingDown => _countdownEnds is not null;

    /// <summary>How much of the countdown at the end is left, 1 to 0, for the screen's ring.</summary>
    public double CountdownLeft { get; private set; }

    /// <summary>A new playback: an episode or not, the profile's settings, and whether the pass-out gate holds.</summary>
    public void Start(bool isEpisode, MarkerSettings settings, bool gated)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _isEpisode = isEpisode;
        _settings = settings;
        _gated = gated;
        _creditsStart = null;
        _lastPosition = null;
        _position = _duration = 0;
        _cardArmed = _screenArmed = _dismissed = _countdownStopped = _ended = false;
        _countdownEnds = null;
        Next = null;
        Show(TimeSpan.Zero);
    }

    /// <summary>Where the credits start, from the playing file's markers; null without.</summary>
    public void TakeCredits(double? creditsStart) => _creditsStart = creditsStart;

    /// <summary>The next episode as resolved, null when there is none. An end reached before starts its countdown now.</summary>
    public void TakeNext(NextEpisode? next, TimeSpan now)
    {
        Next = next;
        if (_ended)
        {
            StartCountdown(now);
        }
        Show(now);
    }

    /// <summary>Follows playback in the title's seconds.</summary>
    public void Update(double position, double duration, TimeSpan now)
    {
        if (_ended)
        {
            return;
        }
        _position = position;
        _duration = duration;
        if (_lastPosition is { } last && position > last && position - last <= PlayingStep)
        {
            if (PromptPoint() is { } point && last < point && position >= point)
            {
                _cardArmed = true;
            }
            double screenPoint = duration - ScreenSecondsBeforeEnd;
            if (_isEpisode && screenPoint > 0 && last < screenPoint && position >= screenPoint)
            {
                _screenArmed = true;
            }
        }
        _lastPosition = position;
        Show(now);
    }

    /// <summary>The title played to its end.</summary>
    public NextUpStep End(TimeSpan now)
    {
        if (_ended)
        {
            return NextUpStep.None;
        }
        _ended = true;
        if (!_isEpisode)
        {
            return NextUpStep.Leave;
        }
        StartCountdown(now);
        Show(now);
        return NextUpStep.None;
    }

    /// <summary>The countdown at the end moves on.</summary>
    public NextUpStep Tick(TimeSpan now)
    {
        if (_countdownEnds is not { } ends)
        {
            return NextUpStep.None;
        }
        if (now >= ends)
        {
            _countdownEnds = null;
            Show(now);
            return NextUpStep.PlayNext;
        }
        Show(now);
        return NextUpStep.None;
    }

    /// <summary>The viewer paused: during the card that means staying, so its countdown stops and the end waits.</summary>
    public void Paused(TimeSpan now)
    {
        if (View == NextUpView.Card)
        {
            _countdownStopped = true;
            Show(now);
        }
    }

    /// <summary>The viewer sought: during the card that is taking back control, as Cancel.</summary>
    public void Sought(TimeSpan now)
    {
        _lastPosition = null;
        if (View == NextUpView.Card)
        {
            Dismiss(now);
        }
    }

    /// <summary>Cancel on the card, Keep watching on the screen: no prompt until the end, and no countdown at it.</summary>
    public void Dismiss(TimeSpan now)
    {
        _dismissed = true;
        _countdownEnds = null;
        Show(now);
    }

    /// <summary>The screen's "Auto-play is on/off": turned on at the end, the countdown starts, as the viewer's own choice.</summary>
    public void SetAutoPlayNext(bool enabled, TimeSpan now)
    {
        _settings = _settings with { AutoPlayNext = enabled };
        if (!enabled)
        {
            _countdownEnds = null;
        }
        else if (_ended)
        {
            _gated = _dismissed = _countdownStopped = false;
            StartCountdown(now);
        }
        Show(now);
    }

    private double? PromptPoint() =>
        _creditsStart ?? (_settings.NextUpPromptSeconds > 0 && _duration > _settings.NextUpPromptSeconds
            ? _duration - _settings.NextUpPromptSeconds
            : null);

    private void StartCountdown(TimeSpan now)
    {
        if (Next is not null && _settings.AutoPlayNext && !_gated && !_dismissed && !_countdownStopped && _countdownEnds is null)
        {
            _countdownEnds = now + TimeSpan.FromSeconds(CountdownSeconds);
        }
    }

    private void Show(TimeSpan now)
    {
        View = (_ended || (_screenArmed && !_dismissed)) && _isEpisode ? NextUpView.Screen
            : _cardArmed && !_ended && !_dismissed && Next is not null ? NextUpView.Card
            : NextUpView.None;
        Seconds = View switch
        {
            NextUpView.Card when _settings.AutoPlayNext && !_gated && !_countdownStopped => (int)Math.Ceiling(Math.Max(0, _duration - _position)),
            NextUpView.Screen when _countdownEnds is { } ends => (int)Math.Ceiling(Math.Max(0, (ends - now).TotalSeconds)),
            _ => null,
        };
        CountdownLeft = _countdownEnds is { } end ? Math.Clamp((end - now).TotalSeconds / CountdownSeconds, 0, 1) : 0;
    }
}
