namespace Granary.App.Services;

/// <summary>
/// The player, over the window rather than in its frame, so it can go on in a bar at the bottom while the viewer browses
/// (<see cref="Views.PlayerPage"/>, MainWindow.Player.cs). Navigation hands it the player's route and, while it fills
/// the window, Back.
/// </summary>
public interface IPlayerOverlay
{
    /// <summary>A title plays.</summary>
    bool IsPlayerOpen { get; }

    /// <summary>The player fills the window; otherwise it is the bar at the bottom, or closed.</summary>
    bool IsPlayerFull { get; }

    /// <summary>Plays <paramref name="request"/> over the window, after the title that played.</summary>
    void Play(object? request);

    /// <summary>Stops playback and takes the player away; the page under it shows.</summary>
    void ClosePlayer();
}
