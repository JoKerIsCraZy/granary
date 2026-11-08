using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Granary.Core.ViewModels;

// The line under the title. The times, "31:55 / 48:41" as the web player writes them, whose end a click turns into the
// time left; Granary's own is that this PC keeps that choice for the next player (IPlaybackDevicePreferences.TimeLeft),
// where the web forgets it. After them, also Granary's own, as none of Silo's clients has it: the clock time the title
// ends, "Ends 22:41".
public sealed partial class PlayerViewModel
{
    // How often the end is worked out again while paused or waiting: it moves later as the clock runs on.
    private static readonly TimeSpan EndsAtTick = TimeSpan.FromSeconds(1);

    // The end is rounded again only once it moved this far, so positions that arrive a moment early or late cannot turn
    // the minute back and forth when the end falls at the half minute.
    private static readonly TimeSpan EndsAtSlack = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan EndsAtHorizon = TimeSpan.FromDays(1);

    // Runs only while paused or waiting. Playing, the end stands still and the engine's positions move it; a tick
    // between them would read a position a moment old as the time left.
    private ITimer? _endsAtTicks;

    // The end the minute shown was rounded from, and that minute; null while no end shows.
    private DateTimeOffset? _endsAtBasis;
    private DateTimeOffset? _endsAt;

    // The zone and culture the end was written in: Windows' time settings changed, it is written again.
    private TimeZoneInfo? _endsAtZone;
    private CultureInfo? _endsAtCulture;

    // The engine's latest load as the title began, and whether a position of the title's own has come since: until then
    // the position is the title before's, whose end would be the new one's wrong end.
    private long _endsAtLoadsBefore;
    private bool _endsAtHasPosition;

    /// <summary>The times end with the time left ("-16:46") instead of the duration, as the viewer last chose on this PC.</summary>
    [ObservableProperty]
    public partial bool ShowsTimeLeft { get; private set; }

    /// <summary>
    /// "Ends 22:41": when what plays ends at its speed, to the minute, in this PC's short time (12 or 24 hours as Windows
    /// writes it). Empty while nothing plays toward a known end: before the title's first position, a length unknown or 0,
    /// a stream without one, the end reached, the player left.
    /// </summary>
    [ObservableProperty]
    public partial string EndsAtText { get; private set; } = "";

    /// <summary>"Ends at 22:41", for screen readers; empty with <see cref="EndsAtText"/>.</summary>
    [ObservableProperty]
    public partial string EndsAtSpoken { get; private set; } = "";

    /// <summary>A click on the times: their end switches between the duration and the time left, kept for this PC.</summary>
    [RelayCommand]
    private void ToggleTimeLeft()
    {
        ShowsTimeLeft = !ShowsTimeLeft;
        // Never fails: a save that does not work is logged, and the choice holds for this player.
        _ = _devicePreferences?.SetTimeLeftAsync(ShowsTimeLeft);
    }

    // The engine's positions come through ShowEngineState; these are the other things the end follows. The single-value
    // OnPhaseChanged is the sleep timer's.
    partial void OnSpeedChanged(double value) => RoundEndsAtAgain();

    partial void OnPhaseChanged(PlayerPhase oldValue, PlayerPhase newValue) => ShowEndsAt();

    partial void OnHasLeftChanged(bool value) => ShowEndsAt();

    // As a title begins (PlayAsync): no end until a position of its own comes from a load after this one.
    private void ResetEndsAt()
    {
        _endsAtLoadsBefore = _engine.Current.LoadId;
        _endsAtHasPosition = false;
        ShowEndsAt();
    }

    // A seek or another speed moves the end for good, however little: the slack is for positions that come early or
    // late, so the minute is worked out again from the new end.
    private void RoundEndsAtAgain()
    {
        _endsAtBasis = null;
        ShowEndsAt();
    }

    // Playing, the end stays where it is as the position moves on; paused or waiting it moves later, which the ticks
    // follow. It is only worked out on the UI thread.
    private void ShowEndsAt()
    {
        if (!_endsAtHasPosition && _engineState is { HasStarted: true, LoadId: var load } && load > _endsAtLoadsBefore)
        {
            _endsAtHasPosition = true;
        }
        if (EndsAtNow() is not { } ends)
        {
            StopTimer(ref _endsAtTicks);
            _endsAtBasis = null;
            SetEndsAt(null);
            return;
        }
        if (_endsAtBasis is not { } basis || (ends - basis).Duration() >= EndsAtSlack)
        {
            _endsAtBasis = ends;
            SetEndsAt(new DateTimeOffset((ends.UtcTicks + TimeSpan.TicksPerMinute / 2) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute,
                TimeSpan.Zero));
        }
        else
        {
            SetEndsAt(_endsAt);
        }
        if (IsPaused || IsBuffering)
        {
            _endsAtTicks ??= _time.CreateTimer(_ => _dispatcher.Post(ShowEndsAt), null, EndsAtTick, EndsAtTick);
        }
        else
        {
            StopTimer(ref _endsAtTicks);
        }
    }

    // Now plus what is left of the title at the speed it plays; null while nothing plays toward a known end. A length
    // that is not a number or endless is a stream's without one, and an end a day or more away would not say which day.
    // Nothing is left once the engine holds at the end of the file or the position reached the length, which can come
    // before the player says the title ended: "Ends" with the time it is now would say nothing. A seek on its way counts
    // as done, as for the skips, so a position from before it does not take the end back.
    private DateTimeOffset? EndsAtNow()
    {
        if (_disposed || HasLeft || Phase != PlayerPhase.Playing || !_endsAtHasPosition || _engineState.IsAtEnd || !(DurationSeconds > 0))
        {
            return null;
        }
        double left = (DurationSeconds - (PendingSeek ?? PositionSeconds)) / (Speed > 0 ? Speed : 1);
        return left > 0 && left < EndsAtHorizon.TotalSeconds ? _time.GetUtcNow() + TimeSpan.FromSeconds(left) : null;
    }

    // Written again when the minute changed, or the zone or the time format did: in this PC's zone and short time ("t",
    // the culture's short time pattern, which follows Windows' own time format).
    private void SetEndsAt(DateTimeOffset? minute)
    {
        var zone = _time.LocalTimeZone;
        var culture = CultureInfo.CurrentCulture;
        if (minute == _endsAt && ReferenceEquals(zone, _endsAtZone) && ReferenceEquals(culture, _endsAtCulture))
        {
            return;
        }
        _endsAt = minute;
        _endsAtZone = zone;
        _endsAtCulture = culture;
        string time = minute is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("t", culture) : "";
        EndsAtText = minute is null ? "" : "Ends " + time;
        EndsAtSpoken = minute is null ? "" : "Ends at " + time;
    }

    // For good, as the player is disposed: a tick already posted finds it disposed and starts none.
    private void StopEndsAt() => StopTimer(ref _endsAtTicks);
}
