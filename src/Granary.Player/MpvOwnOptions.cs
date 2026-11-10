using System.Globalization;
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

    /// <summary>The mode in which mpv applies the pseudo-GUI profile, which holds Granary's options, over the config files.</summary>
    public const string OperationMode = "player-operation-mode";

    /// <summary>The profile mpv applies over the config files inside <c>mpv_initialize</c> in the pseudo-GUI mode.</summary>
    public const string ProfileName = "pseudo-gui";

    /// <summary>
    /// The options Granary sets at every start, in the order it always has; mpv must know each. Without a mpv.conf these
    /// are all it sets, besides <c>hwdec</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Core(VideoOutputKind output, (int Width, int Height) size)
    {
        var own = new List<KeyValuePair<string, string>>
        {
            new("keep-open", "yes"),
            new("idle", "yes"),
            new("input-default-bindings", "no"),
            new("input-vo-keyboard", "no"),
            new("osc", "no"),
            new("ytdl", "no"),
            new("sub-auto", "no"),
            new("rebase-start-time", "yes"),
            // Granary drives System Media Transport Controls itself.
            new("media-controls", "no"),
            // The server keeps the position. mpv must not read or write watch-later files, which would store the URLs,
            // the media proxy's secret among them.
            new("resume-playback", "no"),
            new("save-position-on-quit", "no"),
            // The media proxy resumes a stream the server breaks off itself; mpv's HTTP reader sees a break only when the
            // proxy gave up or a first request was cut, and asks again from its byte, at once. It counts every retry since
            // the last seek against this limit, one that brought data too, so across a long title its default of 5 ended a
            // stream after five such breaks. 20 leaves room for those without keeping mpv busy with a server that is gone.
            new("curl-max-retries", "20"),
        };
        switch (output)
        {
            case VideoOutputKind.D3D11Composition:
                own.Add(new("vo", "gpu-next"));
                own.Add(new("gpu-api", "d3d11"));
                own.Add(new("gpu-context", "d3d11"));
                own.Add(new("d3d11-output-mode", "composition"));
                own.Add(new(CompositionSize, MpvPlaybackEngine.SizeText(size)));
                own.Add(new("force-window", "yes"));
                break;
            case VideoOutputKind.None:
                own.Add(new("vo", "null"));
                own.Add(new("ao", "null"));
                break;
        }
        return own;
    }

    /// <summary>
    /// Options only a mpv.conf could change, kept with one: the stream's address stays out of mpv's own files, logs,
    /// proxies, programs and scripts; mpv reads no key bindings and runs no commands of the file's; it never makes itself
    /// Windows' player for media files. They are mpv's defaults, so Granary sets them only with a file, each only if this
    /// mpv knows it.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Guarded(VideoOutputKind output)
    {
        var own = new List<KeyValuePair<string, string>>
        {
            new("save-watch-history", "no"),
            new("watch-later-dir", ""),
            new("write-filename-in-watch-later-config", "no"),
            new("log-file", ""),
            new("dump-stats", ""),
            new("stream-record", ""),
            new("stream-dump", ""),
            new("http-proxy", ""),
            new("stream-lavf-o", ""),
            new("demuxer-lavf-o", ""),
            new("input-ipc-server", ""),
            // An empty input.conf of Granary's: with none named, mpv would read the folder's.
            new("input-conf", "memory://"),
            new("input-commands", ""),
            new("load-scripts", "no"),
            new("scripts", ""),
            new("register", "no"),
            new("unregister", "no"),
        };
        if (output == VideoOutputKind.None)
        {
            // The pseudo-GUI profile mpv applies first would make a window otherwise.
            own.Add(new("force-window", "no"));
        }
        return own;
    }

    /// <summary>
    /// Options a mpv.conf may not set anywhere, at the top, in any profile or in an include: mpv acts on each as soon as it
    /// is set, before Granary could set it back. A profile that set <c>log-file</c> had mpv write its log, the stream's
    /// address among it, before the set-back. They write the stream's address, which carries the media proxy's secret, or
    /// its content to a file, a socket or a proxy, run scripts, programs or commands, read key bindings, or make mpv
    /// Windows' player for media files. Each with the value a file may still write (Granary's own, or mpv's default), or
    /// null when any value is refused. Screenshots are left: nothing can take one, with no bindings, commands or scripts.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Refused { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        // mpv's own files: its log, statistics, watch-later files and watch history.
        ["log-file"] = "",
        ["dump-stats"] = "",
        ["watch-later-dir"] = "",
        ["write-filename-in-watch-later-config"] = "no",
        ["save-watch-history"] = "no",
        ["watch-history-path"] = null,
        // The stream's content written out, or read through a proxy.
        ["stream-record"] = "",
        ["stream-dump"] = "",
        ["demuxer-cache-dir"] = "",
        ["cache-on-disk"] = "no",
        ["ao-pcm-file"] = null,
        ["vo-image-outdir"] = null,
        ["http-proxy"] = "",
        ["stream-lavf-o"] = "",
        ["demuxer-lavf-o"] = "",
        // Other programs, scripts and commands; ytdl hands the address to a downloader.
        ["input-ipc-server"] = "",
        ["input-ipc-client"] = "",
        ["input-conf"] = null,
        ["input-commands"] = "",
        ["load-scripts"] = "no",
        ["scripts"] = "",
        ["ytdl"] = "no",
        ["register"] = "no",
        ["unregister"] = "no",
    };

    /// <summary>
    /// The pseudo-GUI profile with Granary's options. mpv applies it in <c>mpv_initialize</c> right after the config files,
    /// and over them, when <c>player-operation-mode</c> is <c>pseudo-gui</c>; nothing else runs in between.
    /// </summary>
    public static string Profile(IEnumerable<KeyValuePair<string, string>> own)
    {
        var text = new StringBuilder("[").Append(ProfileName).Append("]\n");
        foreach (var (name, value) in own)
        {
            text.Append(Written(name, value)).Append('=').Append(value).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// A list option Granary keeps empty. Set to "", mpv holds one empty entry instead (an empty command it complains
    /// about), so it is cleared with mpv's "-clr" action in the profile and for each title, and with change-list later.
    /// </summary>
    public static bool IsClearedList(string name, string value) => name == "input-commands" && value.Length == 0;

    private static string Written(string name, string value) => IsClearedList(name, value) ? name + "-clr" : name;

    /// <summary>
    /// Granary's options as options of one file in <c>loadfile</c>. mpv applies these after the file's <c>protocol.*</c>
    /// and <c>extension.*</c> profiles and before it opens the stream. Each value is quoted by its length (<c>%n%</c>), so
    /// nothing in one can end it.
    /// </summary>
    public static IEnumerable<string> PerFile(IEnumerable<KeyValuePair<string, string>> own) =>
        own.Where(o => o.Key != CompositionSize)
            .Select(o => string.Create(CultureInfo.InvariantCulture, $"{Written(o.Key, o.Value)}=%{Encoding.UTF8.GetByteCount(o.Value)}%{o.Value}"));

    /// <summary>Whether mpv's value is Granary's: mpv may write a value in another case or with spaces around it.</summary>
    public static bool Same(string? value, string own) =>
        value is not null && string.Equals(value.Trim(), own, StringComparison.OrdinalIgnoreCase);
}
