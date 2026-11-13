namespace Granary.Core.Controller;

public enum PadActionKind
{
    /// <summary>Focus moves one step in <see cref="PadAction.Direction"/>.</summary>
    MoveFocus,

    /// <summary>The focused element is pressed.</summary>
    Activate,

    /// <summary>Back: an open menu or dialog closes first; the player hides its bar, then closes.</summary>
    Back,

    /// <summary>The focused item's menu.</summary>
    ContextMenu,
    Search,

    /// <summary>The focused card plays or resumes.</summary>
    PlayFocused,
    ToggleSidebar,
    PreviousTab,
    NextTab,
    PageUp,
    PageDown,

    /// <summary>The page scrolls by <see cref="PadAction.X"/> and <see cref="PadAction.Y"/> (up positive); focus stays.</summary>
    Scroll,
    ToggleFullScreen,

    /// <summary>A on the picture: plays or pauses, and the bar shows.</summary>
    PlayPause,

    /// <summary>X: plays or pauses wherever focus is.</summary>
    TogglePause,

    /// <summary>A while Skip Intro or the next episode's card shows.</summary>
    TriggerPrompt,

    /// <summary>The bar shows and takes focus.</summary>
    EnterBar,

    /// <summary>A seek under way would land <see cref="PadAction.Amount"/> seconds from where it began; nothing seeks yet.</summary>
    SeekPreview,

    /// <summary>The one seek: <see cref="PadAction.Amount"/> seconds from where it began.</summary>
    SeekCommit,

    /// <summary>The seek under way came back to where it began: nothing seeks.</summary>
    SeekCancel,
    PreviousChapter,
    NextChapter,

    /// <summary>The volume changes by <see cref="PadAction.Amount"/>.</summary>
    Volume,
    AudioAndSubtitles,
    PlayerSettings,
    PlaybackInfo,
    ToggleSubtitles,
}

/// <summary>One thing a pad asked for (<see cref="PadInterpreter"/>).</summary>
/// <param name="IsRepeat">The step comes from a held direction, not a new press.</param>
public readonly record struct PadAction(
    PadActionKind Kind,
    PadDirection Direction = PadDirection.None,
    double X = 0,
    double Y = 0,
    bool IsRepeat = false)
{
    /// <summary>Seconds of a seek, or steps of the volume.</summary>
    public double Amount => X;

    public static PadAction Move(PadDirection direction, bool isRepeat = false) =>
        new(PadActionKind.MoveFocus, direction, IsRepeat: isRepeat);

    public static PadAction Of(PadActionKind kind, double amount) => new(kind, X: amount);
}
