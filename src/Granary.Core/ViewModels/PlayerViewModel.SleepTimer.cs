using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// The sleep timer. Its button, menu and words are the web's SleepTimerMenu (player/components/SleepTimerMenu.tsx, in its
// audiobook player); what it does is the apps' (Apple's SleepTimer, Android's SleepTimerController): a duration counts on
// the clock, whatever plays or pauses meanwhile and into the next episode too, then pauses; leaving the player ends it.
// "End of episode" is the web's "End of chapter" for video: what plays runs to its end, and the next episode does not
// start by itself.
public sealed partial class PlayerViewModel
{
    // The web's durations, in minutes.
    private static readonly int[] SleepTimerPresets = [5, 15, 30, 45, 60];

    private static readonly TimeSpan SleepTick = TimeSpan.FromSeconds(1);

    private ITimer? _sleepTicks;

    // When the duration runs out, on the player's clock (Now); null without one.
    private TimeSpan? _sleepEnds;

    // "End of episode" is on, until a title ends.
    private bool _sleepAtEnd;

    // The duration ran out while the next title loaded: it pauses as soon as it plays.
    private bool _sleepPausePending;

    // The duration the viewer started last here, which is saved as this device's; the running one while _sleepEnds is set.
    private int? _sleepPicked;

    /// <summary>
    /// The bar has the Sleep button, as Settings → Playback → Player buttons has it (<see cref="Bar"/>). Hidden, a timer that
    /// runs ends, as Turn off ends it.
    /// </summary>
    public bool ShowsSleepTimer => Bar.SleepTimer;

    /// <summary>A duration counts down, or "End of episode" is on: the button is white with the amber dot.</summary>
    [ObservableProperty]
    public partial bool IsSleepTimerOn { get; private set; }

    /// <summary>"Sleep", or "Sleep 29:59" while a duration counts down, as the web's button says it.</summary>
    [ObservableProperty]
    public partial string SleepTimerText { get; private set; } = "Sleep";

    /// <summary>
    /// The choice that runs, for screen readers: "30 min" or "End of episode"; empty while the timer is off. Not the
    /// seconds, which would have them say it again every second.
    /// </summary>
    [ObservableProperty]
    public partial string SleepTimerStatus { get; private set; } = "";

    /// <summary>
    /// The menu: "Turn off" while the timer is on, the durations, and the end of what plays, except in a Watch Party,
    /// whose room decides what plays next, and once that end has come.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<SleepTimerOption> SleepTimerOptions { get; private set; } = [];

    /// <summary>The viewer's pick in the sleep timer menu.</summary>
    public void ChooseSleepTimer(SleepTimerOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        // A row from a menu opened before Settings took the button away: the timer stays off.
        if (!ShowsSleepTimer)
        {
            return;
        }
        switch (option.Action)
        {
            case SleepTimerAction.Minutes when option.Minutes > 0:
                StartSleepTimer(option.Minutes);
                break;
            // A row from a menu opened before the end: that end has come already.
            case SleepTimerAction.EndOfTitle when !IsWatchParty && Phase != PlayerPhase.Ended:
                SleepAtEndOfTitle();
                break;
            case SleepTimerAction.TurnOff:
                StopSleepTimer();
                break;
        }
    }

    // The duration the menu opens on: this device's player.sleep_timer_default_minutes, or the one started here since.
    private int SleepTimerDefault => _sleepPicked ?? _playback?.Preferences.SleepTimerMinutes ?? PlaybackPreferences.DefaultSleepTimerMinutes;

    // Another pick starts again from it; as on Android, the duration is kept as this device's for the next time.
    private void StartSleepTimer(int minutes)
    {
        StopSleepTimer();
        _logger.LogInformation("Sleep timer started: {Minutes} min", minutes);
        _sleepEnds = Now + TimeSpan.FromMinutes(minutes);
        _sleepTicks = _time.CreateTimer(_ => _dispatcher.Post(OnSleepTick), null, SleepTick, SleepTick);
        _sleepPicked = minutes;
        _playback?.SetSleepTimerDefault(minutes);
        ShowSleepTimer();
    }

    // Before the end only: the menu offers it until then.
    private void SleepAtEndOfTitle()
    {
        StopSleepTicks();
        _sleepEnds = null;
        _sleepPausePending = false;
        _sleepAtEnd = true;
        _logger.LogInformation("Sleep timer: stopping at the end of the title");
        _nextUp.Hold(true, Now);
        ShowNextUp();
        ShowSleepTimer();
    }

    // Turned off or changed, and as the player is left: the next episode may start by itself again.
    private void StopSleepTimer()
    {
        StopSleepTicks();
        _sleepEnds = null;
        _sleepPausePending = false;
        if (_sleepAtEnd)
        {
            _sleepAtEnd = false;
            _nextUp.Hold(false, Now);
            ShowNextUp();
        }
        ShowSleepTimer();
    }

    private void StopSleepTicks()
    {
        _sleepTicks?.Dispose();
        _sleepTicks = null;
    }

    private void OnSleepTick()
    {
        // Settings has hidden the timer's button, and that change waits behind this tick: it no longer runs out.
        if (_sleepEnds is not { } ends || _buttons?.Current.SleepTimer == false)
        {
            return;
        }
        if (Now < ends)
        {
            // Only the button's text moves: the menu, open or not, stays as it is.
            SleepTimerText = "Sleep " + Countdown(ends - Now);
            return;
        }
        StopSleepTicks();
        _sleepEnds = null;
        ShowSleepTimer();
        SleepNow();
    }

    // As in the apps, what plays pauses, with no fade. A Watch Party's viewer leaves the room instead, which plays on for the
    // others, as Apple's player does: a pause here alone would be the room's to undo.
    private void SleepNow()
    {
        if (IsWatchParty)
        {
            _logger.LogInformation("The sleep timer ran out; leaving the Watch Party");
            CloseCommand.Execute(null);
            return;
        }
        _logger.LogInformation("The sleep timer ran out; pausing");
        switch (Phase)
        {
            // A title the viewer paused stays as it is. After a failure there is nothing to pause, and a movie's end
            // left the player, which ended the timer.
            case PlayerPhase.Playing when !_engine.Current.IsPaused:
                Pause();
                break;
            case PlayerPhase.Preparing:
                _sleepPausePending = true;
                break;
            case PlayerPhase.Ended:
                // The countdown to the next episode stops: it waits for the viewer.
                _nextUp.Hold(true, Now);
                ShowNextUp();
                break;
        }
    }

    // The title the timer ran out on while it loaded now plays.
    private void PauseIfSleptWhileLoading()
    {
        if (_sleepPausePending && Phase == PlayerPhase.Playing)
        {
            _sleepPausePending = false;
            if (!_engine.Current.IsPaused)
            {
                Pause();
            }
        }
    }

    // What plays came to its end: "End of episode" did what it was for and turns off. The hold stays with this title's
    // next-up flow, so the next episode waits; the title after that starts a new flow without it.
    private void EndSleepAtEnd()
    {
        if (_sleepAtEnd)
        {
            _sleepAtEnd = false;
            _logger.LogInformation("The title ended; the sleep timer holds the next one back");
            ShowSleepTimer();
        }
    }

    private void ShowSleepTimer()
    {
        string end = _playback?.Episode is not null ? "End of episode" : "End of movie";
        string? countdown = _sleepEnds is { } ends ? Countdown(ends - Now) : null;
        bool isOn = countdown is not null || _sleepAtEnd;
        IsSleepTimerOn = isOn;
        SleepTimerText = countdown is null ? "Sleep" : "Sleep " + countdown;
        SleepTimerStatus = countdown is not null && _sleepPicked is { } minutes ? MinutesLabel(minutes) : _sleepAtEnd ? end : "";
        var options = SleepOptions(isOn, end);
        if (!options.SequenceEqual(SleepTimerOptions))
        {
            SleepTimerOptions = options;
        }
    }

    // The web's rows; a default that is none of its durations gets a row of its own, in order.
    private List<SleepTimerOption> SleepOptions(bool isOn, string end)
    {
        int preferred = SleepTimerDefault;
        var options = new List<SleepTimerOption>();
        if (isOn)
        {
            options.Add(new SleepTimerOption(SleepTimerAction.TurnOff, 0, "Turn off"));
        }
        var durations = preferred > 0 && !SleepTimerPresets.Contains(preferred) ? SleepTimerPresets.Append(preferred).Order() : SleepTimerPresets.AsEnumerable();
        options.AddRange(durations.Select(minutes =>
            new SleepTimerOption(SleepTimerAction.Minutes, minutes, MinutesLabel(minutes), IsDefault: minutes == preferred)));
        // Not once the title has ended: that end has come.
        if (!IsWatchParty && Phase != PlayerPhase.Ended)
        {
            options.Add(new SleepTimerOption(SleepTimerAction.EndOfTitle, 0, end));
        }
        return options;
    }

    // The end row comes and goes with the title's end.
    partial void OnPhaseChanged(PlayerPhase value) => ShowSleepTimer();

    // Settings → Playback → Player buttons hid the Sleep timer: a timer that runs ends, as Turn off ends it, and a hold it
    // left at the end of what plays (End of episode, or a duration that ran out there) goes with it, so the next episode
    // starts as usual.
    private void EndSleepTimerWithItsButton()
    {
        if (IsSleepTimerOn)
        {
            _logger.LogInformation("Sleep timer turned off with its button in Settings");
        }
        StopSleepTimer();
        if (_nextUp.IsHeld)
        {
            _nextUp.Hold(false, Now);
            ShowNextUp();
        }
    }

    // "30 min", as the web's rows say it.
    private static string MinutesLabel(int minutes) => string.Create(CultureInfo.InvariantCulture, $"{minutes} min");

    // "29:59", as the web's formatCountdown: whole seconds, rounded up.
    private static string Countdown(TimeSpan left)
    {
        int seconds = (int)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
