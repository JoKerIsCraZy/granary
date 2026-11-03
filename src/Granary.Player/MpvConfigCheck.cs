using Granary.Core.Playback;

namespace Granary.Player;

/// <summary>
/// <see cref="IMpvConfigCheck"/> with the engine's own pre-scan (<see cref="MpvConfigText"/>): the same options, kept and
/// refused by the same rules as when mpv starts with the file (MpvPlaybackEngine.Start.cs). Only the text itself is read;
/// what an include adds is checked when mpv starts.
/// </summary>
internal sealed class MpvConfigCheck(MpvEngineOptions options) : IMpvConfigCheck
{
    private const string Include = "include";

    public IReadOnlyList<string> Notes(string text)
    {
        var own = MpvOwnOptions.Core(options.VideoOutput, options.InitialOutputSize).Concat(MpvOwnOptions.Guarded(options.VideoOutput)).ToList();
        var names = own.Select(o => o.Key).Append(MpvOwnOptions.OperationMode).Append(Include).ToHashSet();
        var entries = MpvConfigText.Entries(text, names);
        var kept = own.Where(o => o.Key != MpvOwnOptions.CompositionSize).ToDictionary(StringComparer.Ordinal);
        var notes = new List<string>();
        if (MpvConfigText.Refusal(entries.Where(e => e.Option != Include), kept) is { } reason)
        {
            // Nothing else of the file matters then.
            notes.Add($"This file would not be used: it {reason}.");
        }
        else
        {
            var ownNames = own.Select(o => o.Key).ToHashSet();
            foreach (var set in entries.Where(e => ownNames.Contains(e.Option)).GroupBy(e => e.Option))
            {
                // At the top it holds from the start; in a profile only once that profile applies.
                string? profile = set.All(e => e.Profile is not null) ? set.First().Profile : null;
                notes.Add(profile is null
                    ? $"Granary keeps its own {set.Key}."
                    : $"Granary keeps its own {set.Key}, which the profile {profile} sets.");
            }
        }
        if (entries.Any(e => e.Option == Include))
        {
            notes.Add("Granary checks the files it includes only when the player starts.");
        }
        return notes;
    }
}
