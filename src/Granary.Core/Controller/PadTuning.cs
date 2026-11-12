namespace Granary.Core.Controller;

/// <summary>How far, how long and how fast: the numbers behind <see cref="PadInterpreter"/>.</summary>
public static class PadTuning
{
    /// <summary>Inside these radii a stick counts as at rest (XInput's stock values, 7849 and 8689 of 32767).</summary>
    public const double LeftStickDeadZone = 0.24;
    public const double RightStickDeadZone = 0.265;

    /// <summary>Below this a trigger counts as at rest (XInput's 30 of 255).</summary>
    public const double TriggerDeadZone = 0.12;

    /// <summary>A stick is a direction from here on, and stays one down to <see cref="StickRelease"/>.</summary>
    public const double StickPress = 0.7;
    public const double StickRelease = 0.5;

    /// <summary>A trigger is pressed from here on, and stays so down to <see cref="TriggerRelease"/>.</summary>
    public const double TriggerPress = 0.5;
    public const double TriggerRelease = 0.3;

    public const double VolumeStep = 5;

    /// <summary>A seek that ends within this many seconds of where it began is none.</summary>
    public const double SeekNothing = 0.5;

    /// <summary>Pixels a second the page scrolls with the right stick pushed all the way.</summary>
    public const double ScrollSpeed = 2200;

    /// <summary>A held for this long opens the focused item's menu.</summary>
    public static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(500);

    /// <summary>A held direction steps again after this wait, then at its own pace.</summary>
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan FocusRepeat = TimeSpan.FromMilliseconds(110);
    public static readonly TimeSpan PageRepeat = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan VolumeRepeat = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan SeekRepeat = TimeSpan.FromMilliseconds(200);

    /// <summary>A seek made of steps is sent once nothing was added for this long.</summary>
    public static readonly TimeSpan SeekCommitDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest time one reading stands for: a poll that came late neither scrolls nor scrubs for the gap.</summary>
    public static readonly TimeSpan LongestStep = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The step a held seek adds, the longer it is held the larger: the profile's own step at first, then half a minute,
    /// a minute, three and five.
    /// </summary>
    public static double SeekStep(TimeSpan held, double profileStep) => held.TotalSeconds switch
    {
        < 1.5 => profileStep,
        < 3 => Math.Max(profileStep, 30),
        < 5 => Math.Max(profileStep, 60),
        < 8 => 180,
        _ => 300,
    };

    /// <summary>Seconds of the title a second a pulled trigger scrubs through: slowly when just pressed, fast when pulled home.</summary>
    public static double ScrubRate(double pressure)
    {
        double pull = Math.Clamp((pressure - TriggerRelease) / (1 - TriggerRelease), 0, 1);
        return 5 + (175 * pull * pull);
    }
}
