using System.Globalization;
using System.Text.Json;

namespace Granary.Player;

/// <summary>An option a mpv.conf sets: in a profile, or outside one (null).</summary>
internal readonly record struct MpvConfigEntry(string Option, string? Profile, string Value);

/// <summary>
/// What a viewer's mpv.conf sets of Granary's options, read the way mpv reads it, so the log can name them and a file
/// that would undo them is refused. mpv itself applies the file.
/// </summary>
internal static class MpvConfigText
{
    // mpv's actions on list options ("glsl-shaders-append").
    private static readonly string[] ListSuffixes = ["-add", "-append", "-pre", "-set", "-clr", "-del", "-remove", "-toggle"];

    /// <summary>
    /// The options of <paramref name="names"/> in a config file, by mpv's own reading of a line (options/parse_configfile.c):
    /// "#" starts a comment and "[name]" a profile; an option's name, after an optional "--", is made of letters, digits,
    /// "-" and "_"; its value follows "=", in quotes, after its length (<c>%5%value</c>), or up to a "#". "no-osc" is osc
    /// set to no, a bare flag is set to yes, and "scripts-append" and the alias "script" name scripts.
    /// </summary>
    public static IReadOnlyList<MpvConfigEntry> Entries(string text, IReadOnlySet<string> names)
    {
        var found = new List<MpvConfigEntry>();
        string? profile = null;
        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (line[0] == '[')
            {
                int end = line.IndexOf(']', StringComparison.Ordinal);
                profile = Section(end > 0 ? line[1..end] : "");
                continue;
            }
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                line = line[2..];
            }
            int length = 0;
            while (length < line.Length && (char.IsAsciiLetterOrDigit(line[length]) || line[length] is '-' or '_'))
            {
                length++;
            }
            string rest = line[length..].TrimStart();
            string? value = rest.StartsWith('=') ? Value(rest[1..].TrimStart()) : null;
            if (Named(line[..length], value, names) is { } entry)
            {
                found.Add(entry with { Profile = profile });
            }
        }
        return found;
    }

    /// <summary>
    /// The options of <paramref name="names"/> in mpv's profiles after it started, from its <c>profile-list</c>, includes
    /// read: the pseudo-GUI profile's beyond mpv's own entry and Granary's, and every other profile's. A profile keeps an
    /// option as it was written; a bare flag has an empty value there.
    /// </summary>
    public static IReadOnlyList<MpvConfigEntry> FromProfiles(string? profileList, IReadOnlyList<KeyValuePair<string, string>> own,
        IReadOnlySet<string> names)
    {
        var found = new List<MpvConfigEntry>();
        if (string.IsNullOrEmpty(profileList))
        {
            return found;
        }
        using var document = JsonDocument.Parse(profileList);
        foreach (var profile in document.RootElement.EnumerateArray())
        {
            string name = profile.GetProperty("name").GetString() ?? "";
            var options = profile.GetProperty("options").EnumerateArray()
                .Select(o => new KeyValuePair<string, string>(o.GetProperty("key").GetString() ?? "", o.GetProperty("value").GetString() ?? ""))
                .ToList();
            IEnumerable<KeyValuePair<string, string>> theirs = name == MpvOwnOptions.ProfileName ? AfterGranarys(options, own) : options;
            foreach (var (key, value) in theirs)
            {
                if (Named(key, value, names) is { } entry)
                {
                    found.Add(entry with { Profile = Section(name) });
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Why mpv must not start with these options, or null when it may: an option that turns off the profile holding
    /// Granary's options, the file's own pseudo-GUI profile setting one of them after Granary's, or a profile running
    /// commands, which no set-back can take back.
    /// </summary>
    public static string? Refusal(IEnumerable<MpvConfigEntry> entries, IReadOnlyDictionary<string, string> kept)
    {
        foreach (var (option, profile, value) in entries)
        {
            if (option == MpvOwnOptions.OperationMode && !MpvOwnOptions.Same(value, MpvOwnOptions.ProfileName))
            {
                return $"sets {MpvOwnOptions.OperationMode}, which Granary needs to keep its own options";
            }
            if (profile == MpvOwnOptions.ProfileName && kept.TryGetValue(option, out string? own) && !MpvOwnOptions.Same(value, own))
            {
                return $"sets {option} in its [{MpvOwnOptions.ProfileName}] profile, which mpv applies over Granary's own options";
            }
            if (profile is not null && option == "input-commands" && value.Length > 0)
            {
                return $"sets input-commands in its profile {profile}, which mpv runs as soon as the profile applies";
            }
        }
        return null;
    }

    // "[default]" is the file's own options.
    private static string? Section(string name) => name is "" or "default" ? null : name;

    // The pseudo-GUI profile holds mpv's own entry, then Granary's options, then the file's section; the file's part.
    private static IEnumerable<KeyValuePair<string, string>> AfterGranarys(List<KeyValuePair<string, string>> options,
        IReadOnlyList<KeyValuePair<string, string>> own)
    {
        int start = options.FindIndex(o => own.Count > 0 && o.Key == own[0].Key && o.Value == own[0].Value);
        if (start < 0 || start + own.Count > options.Count)
        {
            // Granary's options are not where they belong: all of it is checked.
            return options;
        }
        return options.Skip(start + own.Count);
    }

    private static MpvConfigEntry? Named(string option, string? value, IReadOnlySet<string> names)
    {
        if (names.Contains(option))
        {
            return new(option, null, value ?? "yes");
        }
        if (option.StartsWith("no-", StringComparison.Ordinal) && names.Contains(option[3..]))
        {
            return new(option[3..], null, "no");
        }
        if (option == "script" && names.Contains("scripts"))
        {
            return new("scripts", null, value ?? "");
        }
        foreach (string suffix in ListSuffixes)
        {
            if (option.EndsWith(suffix, StringComparison.Ordinal) && names.Contains(option[..^suffix.Length]))
            {
                return new(option[..^suffix.Length], null, value ?? "");
            }
        }
        return null;
    }

    // A value as mpv reads it: in quotes, after its length, or up to a comment.
    private static string Value(string text)
    {
        if (text.Length > 0 && text[0] is '"' or '\'')
        {
            int end = text.IndexOf(text[0], 1);
            return end > 0 ? text[1..end] : text[1..];
        }
        if (text.StartsWith('%'))
        {
            int mark = text.IndexOf('%', 1);
            // Digits only: a sign or a size past the line is no length, and the value reads as text, as mpv refuses it.
            if (mark > 1 && int.TryParse(text.AsSpan(1, mark - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                && length <= text.Length - mark - 1)
            {
                return text.Substring(mark + 1, length);
            }
        }
        int comment = text.IndexOf('#', StringComparison.Ordinal);
        return (comment < 0 ? text : text[..comment]).Trim();
    }
}
