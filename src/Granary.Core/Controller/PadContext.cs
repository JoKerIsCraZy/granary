namespace Granary.Core.Controller;

/// <summary>What the pad drives: the pages (the minimized player's bar is part of them), or the full player.</summary>
public enum PadMode
{
    Browsing,
    Player,
}

/// <summary>Where focus is in the full player.</summary>
public enum PadFocus
{
    /// <summary>On the picture: the D-pad seeks and A plays or pauses.</summary>
    Picture,

    /// <summary>On one of the controls, or on a button over the picture: the D-pad moves between them and A presses.</summary>
    Controls,

    /// <summary>In an open menu or panel, which keeps the pad until it closes.</summary>
    Menu,
}

/// <summary>What the window knows when a pad reading comes.</summary>
/// <param name="HasPrompt">Skip Intro or the next episode's card shows: A on the picture takes it.</param>
/// <param name="CanSeek">The title can be sought right now.</param>
/// <param name="SkipBackSeconds">The profile's step back, the first step of a seek to the left.</param>
/// <param name="SkipForwardSeconds">The profile's step forward.</param>
/// <param name="PositionSeconds">Where the title is, from where a seek begins.</param>
/// <param name="DurationSeconds">The title's length; 0 while unknown, which leaves a seek's end open.</param>
public readonly record struct PadContext(
    PadMode Mode,
    PadFocus Focus = PadFocus.Picture,
    bool HasPrompt = false,
    bool CanSeek = false,
    double SkipBackSeconds = 10,
    double SkipForwardSeconds = 30,
    double PositionSeconds = 0,
    double DurationSeconds = 0)
{
    public static PadContext Browsing => new(PadMode.Browsing);
}
