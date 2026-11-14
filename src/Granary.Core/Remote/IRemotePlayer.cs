namespace Granary.Core.Remote;

/// <summary>
/// The open player as the phone remote sees it: its status and the commands it takes. Every member is used on the UI
/// thread only.
/// </summary>
public interface IRemotePlayer
{
    /// <summary>What the phones are shown now.</summary>
    SiloCastState State { get; }

    /// <summary>Something the phones are shown changed, other than the position alone.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Carries out a phone's command through the player's own commands, so a Watch Party's rules hold. Required: every number
    /// a phone sends is range-checked and clamped before it reaches the player, as nothing before here checks it:
    /// <c>seconds</c> into the title, <c>trackId</c> to a track of the list last sent (else <see cref="RemoteCommandResult.Ignored"/>),
    /// <c>speed</c> to the player's speeds, <c>volume</c> to 0 to 1, <c>milliseconds</c> to the subtitle delay's limits; a
    /// value that is not finite is ignored.
    /// </summary>
    RemoteCommandResult Apply(SiloCastMessage.Control command);
}

/// <summary>How the player took a phone's command.</summary>
public enum RemoteCommandResult
{
    /// <summary>Carried out: every phone gets the new status.</summary>
    Done,

    /// <summary>Nothing to do (play while playing, a command without its argument): no answer.</summary>
    Ignored,

    /// <summary>No title is ready for it: <c>error player_not_ready</c>.</summary>
    NotReady,

    /// <summary>The player refused it (no next episode, a Watch Party's rules): <c>error command_failed</c>.</summary>
    Failed,
}
