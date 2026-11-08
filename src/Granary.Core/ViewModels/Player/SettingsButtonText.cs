namespace Granary.Core.ViewModels.Player;

/// <summary>
/// What screen readers and the tooltip say of the player's settings button: the web's "Playback settings"
/// (player/components/QualityMenu.tsx), then Granary's own stream method, then the speed while it is not 1×.
/// </summary>
public static class SettingsButtonText
{
    private const string Settings = "Playback settings";

    /// <summary>"Playback settings, Transcode, 1.5×"; just "Playback settings" without a server stream at 1×.</summary>
    public static string Name(string speedBadge, StreamMethod method) => Join(Settings, StreamMethods.Label(method), speedBadge);

    /// <summary>"Playback settings, Direct Play": the speed shows beside the gear, so the tooltip leaves it out.</summary>
    public static string Tip(StreamMethod method) => Join(Settings, StreamMethods.Label(method));

    private static string Join(params string?[] parts) => string.Join(", ", parts.Where(part => !string.IsNullOrEmpty(part)));
}
