using System.Text;

namespace Granary.Player;

/// <summary>
/// The mpv options Granary sets itself and keeps over the viewer's own mpv.conf: what shows the video in Granary's
/// window, what lets Granary drive the player and follow the server, and what keeps the stream's address, which carries
/// the media proxy's secret, inside Granary.
/// </summary>
internal static class MpvOwnOptions
{
    /// <summary>Granary changes this one while it plays, so it is kept at the start only.</summary>
    public const string CompositionSize = "d3d11-composition-size";

    // mpv's actions on list options ("glsl-shaders-append"), and the alias "script" for "scripts-append".
    private static readonly string[] ListSuffixes = ["-add", "-append", "-pre", "-set", "-clr", "-del", "-remove", "-toggle"];

    /// <summary>Granary's options and the values they keep, in the order they are set.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> For(VideoOutputKind output, (int Width, int Height) size)
    {
        var own = new List<KeyValuePair<string, string>>();
        switch (output)
        {
            case VideoOutputKind.D3D11Composition:
                Add("vo", "gpu-next");
                Add("gpu-api", "d3d11");
                Add("gpu-context", "d3d11");
                Add("d3d11-output-mode", "composition");
                Add(CompositionSize, MpvPlaybackEngine.SizeText(size));
                Add("force-window", "yes");
                break;
            case VideoOutputKind.None:
                Add("vo", "null");
                Add("ao", "null");
                Add("force-window", "no");
                break;
        }
        Add("keep-open", "yes");
        Add("idle", "yes");
        Add("input-default-bindings", "no");
        Add("input-vo-keyboard", "no");
        Add("osc", "no");
        Add("ytdl", "no");
        Add("sub-auto", "no");
        Add("rebase-start-time", "yes");
        // Granary drives System Media Transport Controls itself.
        Add("media-controls", "no");
        // The server keeps the position. mpv must not read or write watch-later files or a watch history, which would
        // store the URLs, the media proxy's secret among them; nor write them to a log of its own, or hand them to
        // scripts or other programs.
        Add("resume-playback", "no");
        Add("save-position-on-quit", "no");
        Add("save-watch-history", "no");
        Add("log-file", "");
        Add("input-ipc-server", "");
        Add("load-scripts", "no");
        Add("scripts", "");
        // Nor make itself Windows' player for media files.
        Add("register", "no");
        Add("unregister", "no");
        return own;

        void Add(string name, string value) => own.Add(new(name, value));
    }

    /// <summary>
    /// The pseudo-GUI profile with Granary's options. mpv applies it in <c>mpv_initialize</c> right after the config files,
    /// and over them, when <c>player-operation-mode</c> is <c>pseudo-gui</c>; nothing else runs in between.
    /// </summary>
    public static string Profile(IEnumerable<KeyValuePair<string, string>> own)
    {
        var text = new StringBuilder("[pseudo-gui]\n");
        foreach (var (name, value) in own)
        {
            text.Append(name).Append('=').Append(value).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// The options of <paramref name="names"/> that a config file sets, each with the profile it is in (null outside
    /// one), by mpv's own reading of a line (options/parse_configfile.c): "#" starts a comment, "[name]" a profile, and an
    /// option's name, after an optional "--", is made of letters, digits, "-" and "_". "no-osc" names osc and
    /// "scripts-append" scripts. Only for the log: mpv reads the file itself, includes too.
    /// </summary>
    public static IReadOnlyList<(string Option, string? Profile)> SetIn(string text, IReadOnlySet<string> names)
    {
        var found = new List<(string, string?)>();
        string? profile = null;
        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.TrimStart();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (line[0] == '[')
            {
                int end = line.IndexOf(']', StringComparison.Ordinal);
                string name = end > 0 ? line[1..end] : "";
                // [default] is the file's own options.
                profile = name is "" or "default" ? null : name;
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
            if (OwnName(line[..length], names) is { } option && !found.Contains((option, profile)))
            {
                found.Add((option, profile));
            }
        }
        return found;
    }

    private static string? OwnName(string option, IReadOnlySet<string> names)
    {
        if (names.Contains(option))
        {
            return option;
        }
        if (option.StartsWith("no-", StringComparison.Ordinal) && names.Contains(option[3..]))
        {
            return option[3..];
        }
        if (option == "script" && names.Contains("scripts"))
        {
            return "scripts";
        }
        foreach (string suffix in ListSuffixes)
        {
            if (option.EndsWith(suffix, StringComparison.Ordinal) && names.Contains(option[..^suffix.Length]))
            {
                return option[..^suffix.Length];
            }
        }
        return null;
    }
}
