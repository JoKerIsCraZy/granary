namespace Granary.Core.ViewModels.Player;

/// <summary>What keeps the player's controls up once the idle wait has run out; the first that applies is named in the log.</summary>
public enum ControlsHold
{
    /// <summary>Nothing: the controls hide, wherever the pointer rests in the full player.</summary>
    None,

    /// <summary>The player is the minimized bar, which always shows its controls.</summary>
    Docked,

    /// <summary>The pointer is over the mini view, whose play button and way back fill its small window.</summary>
    MiniView,

    /// <summary>The title is preparing, failed or ended.</summary>
    NotPlaying,

    Paused,
    Buffering,
    SeekDrag,

    /// <summary>A menu or flyout of the controls is open.</summary>
    Menu,

    /// <summary>A control in them was reached with the keyboard.</summary>
    KeyboardFocus,

    /// <summary>The subtitle panels.</summary>
    Panel,

    MarkerEditor,
}

/// <summary>
/// The player as its idle wait runs out (PlayerPage.AutoHide.cs). As on Silo's web player the controls hide 3 s after the
/// last move or key wherever the pointer rests, on them or at the edge of the screen, and wait only while a menu is open or
/// the title is paused (the web's <c>resetControlsTimer</c> in <c>player/components/VideoPlayer.tsx</c>); here also while it
/// loads or buffers, during a seek drag, with a control reached by keyboard, a panel open or the markers being edited. The
/// mini view, the web's Picture in Picture, keeps them while the pointer is over its window.
/// </summary>
public readonly record struct PlayerControlsIdle
{
    public bool IsDocked { get; init; }

    public bool IsMiniView { get; init; }

    /// <summary>The pointer is over the player's window; only the mini view counts it.</summary>
    public bool IsPointerOverWindow { get; init; }

    public bool IsPlaying { get; init; }

    public bool IsPaused { get; init; }

    public bool IsBuffering { get; init; }

    public bool IsDraggingSeekBar { get; init; }

    public bool HasOpenMenu { get; init; }

    /// <summary>A control in them has keyboard focus.</summary>
    public bool HasKeyboardFocus { get; init; }

    /// <summary>A remote's arrows move between the controls: its focus keeps nothing up, as on Silo's TV app.</summary>
    public bool MovesThroughControls { get; init; }

    public bool IsPanelOpen { get; init; }

    public bool IsEditingMarkers { get; init; }

    /// <summary>What keeps the controls up, or <see cref="ControlsHold.None"/> when they hide.</summary>
    public ControlsHold Hold => this switch
    {
        { IsDocked: true } => ControlsHold.Docked,
        { IsMiniView: true, IsPointerOverWindow: true } => ControlsHold.MiniView,
        { IsPlaying: false } => ControlsHold.NotPlaying,
        { IsPaused: true } => ControlsHold.Paused,
        { IsBuffering: true } => ControlsHold.Buffering,
        { IsDraggingSeekBar: true } => ControlsHold.SeekDrag,
        { HasOpenMenu: true } => ControlsHold.Menu,
        { HasKeyboardFocus: true, MovesThroughControls: false } => ControlsHold.KeyboardFocus,
        { IsPanelOpen: true } => ControlsHold.Panel,
        { IsEditingMarkers: true } => ControlsHold.MarkerEditor,
        _ => ControlsHold.None,
    };
}
