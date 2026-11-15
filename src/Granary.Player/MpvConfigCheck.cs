using Granary.Core.Playback;
using Granary.Core.Texts;

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
        // The names and the options kept as StartWith and Configure have them.
        var own = MpvOwnOptions.Core(options.VideoOutput, options.InitialOutputSize).Concat(MpvOwnOptions.Guarded(options.VideoOutput)).ToList();
        var names = own.Select(o => o.Key).Append(MpvOwnOptions.OperationMode).Concat(MpvOwnOptions.Refused.Keys).Append(Include).ToHashSet();
        var entries = MpvConfigText.Entries(text, names);
        var kept = own.Where(o => o.Key != MpvOwnOptions.CompositionSize && !MpvOwnOptions.Refused.ContainsKey(o.Key))
            .ToDictionary(StringComparer.Ordinal);
        var notes = new List<string>();
        if (MpvConfigText.Refusal(entries.Where(e => e.Option != Include), kept) is { } reason)
        {
            // Nothing else of the file matters then.
            // The reason in English, as the log and Settings' line on how mpv.conf fared have it.
            notes.Add(AppText.Get("granary.settings.mpv.editor.notUsed", ("reason", "it " + reason)));
        }
        else
        {
            // Those a file may set and Granary sets back; a refused one at its allowed value changes nothing, nor does one at
            // Granary's own.
            foreach (var set in entries.Where(e => e.Option == MpvOwnOptions.CompositionSize
                || (kept.TryGetValue(e.Option, out string? ours) && !MpvOwnOptions.Same(e.Value, ours))).GroupBy(e => e.Option))
            {
                // At the top it holds from the start; in a profile only once that profile applies.
                string? profile = set.All(e => e.Profile is not null) ? set.First().Profile : null;
                notes.Add(profile is null
                    ? AppText.Get("granary.settings.mpv.editor.keeps", ("option", set.Key))
                    : AppText.Get("granary.settings.mpv.editor.keepsFromProfile", ("option", set.Key), ("profile", profile)));
            }
        }
        if (entries.Any(e => e.Option == Include))
        {
            notes.Add(AppText.Get("granary.settings.mpv.editor.includes"));
        }
        return notes;
    }
}
