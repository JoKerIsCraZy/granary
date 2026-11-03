namespace Granary.Core.Playback;

/// <summary>
/// Granary's own reading of a mpv.conf, the one the player makes before mpv starts, for the editor to show before a save:
/// which of its own options Granary keeps over the file, and whether the player would use the file at all.
/// </summary>
public interface IMpvConfigCheck
{
    /// <summary>Short notes in plain words, the most important first; empty when nothing in the text concerns Granary.</summary>
    IReadOnlyList<string> Notes(string text);
}
