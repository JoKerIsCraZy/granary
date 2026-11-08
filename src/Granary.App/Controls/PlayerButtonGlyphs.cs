using Granary.Core.Playback;

namespace Granary.App.Controls;

/// <summary>The icons of the player's bar buttons, as Settings → Playback → Player buttons lists them: the player's own.</summary>
public static class PlayerButtonGlyphs
{
    /// <summary>
    /// Segoe Fluent Icons: Volume3, QuietHours, Audio, CC, BulletedList, Settings, Tag, Info, the mini view's, Fill and
    /// FullScreen. The player draws audio as the web's AudioLines, which no font has: Audio stands in for it.
    /// </summary>
    public static string Of(PlayerButton button) => button switch
    {
        PlayerButton.Volume => "",
        PlayerButton.SleepTimer => "",
        PlayerButton.Audio => "",
        PlayerButton.Subtitles => "",
        PlayerButton.Chapters => "",
        PlayerButton.Settings => "",
        PlayerButton.EditMarkers => "",
        PlayerButton.PlaybackInfo => "",
        PlayerButton.MiniView => "",
        PlayerButton.Fill => "",
        _ => "",
    };
}
