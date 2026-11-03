using System.Diagnostics.CodeAnalysis;
using Granary.Core.Playback;
using Granary.Player.Interop;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

// How mpv starts, with the viewer's own mpv.conf or without (docs/architecture.md, "Your own mpv.conf").
//
// Granary's options (MpvOwnOptions) are set before mpv_initialize. With a mpv.conf in the config folder, mpv reads it
// itself in mpv_initialize (config, config-dir; ~~/ is that folder), after everything set before, so the file could
// override them. mpv acts on some of them as soon as mpv_initialize is done, before any call after it can set them back:
// it makes its video output and leaves idle mode on what the file says. So Granary's options also go into the pseudo-GUI
// profile, which mpv applies over the config files inside mpv_initialize; a profile of the file that sets one later is
// set back as mpv reports it (KeepsOwn).
public sealed partial class MpvPlaybackEngine
{
    // The properties the engine follows.
    private static readonly (string Name, int Format)[] Followed =
    [
        ("time-pos", LibMpv.FormatDouble),
        ("duration", LibMpv.FormatDouble),
        ("pause", LibMpv.FormatFlag),
        ("eof-reached", LibMpv.FormatFlag),
        ("hwdec-current", LibMpv.FormatString),
        ("paused-for-cache", LibMpv.FormatFlag),
        ("volume", LibMpv.FormatDouble),
        ("mute", LibMpv.FormatFlag),
        // Only that it may have changed, several times a second while the stream loads: it is read when the
        // throttle lets it (ReadBuffered), not with every notice.
        (CacheStateProperty, LibMpv.FormatNone),
    ];

    private readonly IReadOnlyList<KeyValuePair<string, string>> _own;
    private MpvClient _mpv;

    // With a mpv.conf: Granary's options that mpv must keep while it runs, by name, and those the log has named as set back.
    // Both are used on mpv's event thread only.
    private Dictionary<string, string>? _kept;
    private readonly HashSet<string> _setBack = [];

    [MemberNotNull(nameof(_mpv))]
    private void Start()
    {
        if (ConfigFile(_options.ConfigDirectory) is { } file)
        {
            try
            {
                Open(file);
                SayWhatIsKept(file);
                return;
            }
            catch (InvalidOperationException ex)
            {
                // mpv refused to start with it at all (an option that only lists the audio devices, say), and has said why:
                // it plays as if there were none.
                _logger.LogWarning("mpv did not start with {File} ({Reason}); it plays without it", file, ex.Message);
            }
        }
        Open(null);
    }

    private static string? ConfigFile(string? directory) =>
        directory is not null && Path.Combine(directory, MpvConfigFolder.ConfigFileName) is var file && File.Exists(file) ? file : null;

    [MemberNotNull(nameof(_mpv))]
    private void Open(string? configFile)
    {
        _kept = null;
        _mpv = new MpvClient(_logger);
        _mpv.PropertyChanged += OnPropertyChanged;
        _mpv.LogMessage += OnLogMessage;
        _mpv.FileEnded += OnFileEnded;
        _mpv.LifecycleEvent += OnLifecycleEvent;
        try
        {
            Configure(configFile);
            _mpv.Initialize(_kept is null ? Followed : [.. Followed, .. _kept.Keys.Select(name => (name, LibMpv.FormatString))]);
        }
        catch
        {
            Close();
            throw;
        }
    }

    private void Configure(string? configFile)
    {
        _mpv.SetOption("hwdec", _options.HardwareDecoding);
        foreach (var (name, value) in _own)
        {
            _mpv.SetOption(name, value);
        }
        if (configFile is null)
        {
            return;
        }
        string folder = Path.GetDirectoryName(configFile)!;
        _mpv.SetOption("config", "yes");
        _mpv.SetOption("config-dir", folder);
        // Compiled shaders are kept in the folder too, not in mpv's own under %LOCALAPPDATA%\mpv. The file may say otherwise.
        string cache = Path.Combine(folder, "cache");
        _mpv.SetOption("gpu-shader-cache-dir", cache);
        _mpv.SetOption("icc-cache-dir", cache);
        _mpv.LoadConfigText(MpvOwnOptions.Profile(_own));
        _mpv.SetOption("player-operation-mode", "pseudo-gui");
        _kept = _own.Where(o => o.Key != MpvOwnOptions.CompositionSize).ToDictionary();
    }

    // Once per start: the file mpv read, and the options of Granary's it sets, which the profile has already set back.
    private void SayWhatIsKept(string configFile)
    {
        _logger.LogInformation("mpv started with {File}", configFile);
        string text;
        try
        {
            text = File.ReadAllText(configFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "mpv.conf could not be read again to name the options Granary keeps");
            return;
        }
        var names = _own.Select(o => o.Key).ToHashSet();
        foreach (var (option, profile) in MpvOwnOptions.SetIn(text, names))
        {
            if (profile is null)
            {
                _logger.LogWarning("mpv.conf sets {Option}; Granary keeps its own", option);
            }
            else
            {
                _logger.LogWarning("mpv.conf's profile {Profile} sets {Option}; Granary keeps its own", profile, option);
            }
        }
    }

    // A notice of one of Granary's options while mpv runs with a mpv.conf: a profile of the file (profile-cond, or one
    // applied by name) set it, or the file's own [pseudo-gui] did after Granary's. It is set back. True for these names.
    private bool KeepsOwn(string name, object? value)
    {
        if (_kept is not { } kept || !kept.TryGetValue(name, out string? own))
        {
            return false;
        }
        if (value is string current && current != own)
        {
            if (_setBack.Add(name))
            {
                _logger.LogWarning("mpv.conf set {Option}; Granary keeps its own", name);
            }
            int rc = _mpv.SetProperty(name, own);
            if (rc < 0)
            {
                _logger.LogWarning("mpv did not take Granary's {Option} back: {Error}", name, LibMpv.Error(rc));
            }
        }
        return true;
    }

    private void Close()
    {
        _mpv.PropertyChanged -= OnPropertyChanged;
        _mpv.LogMessage -= OnLogMessage;
        _mpv.FileEnded -= OnFileEnded;
        _mpv.LifecycleEvent -= OnLifecycleEvent;
        _mpv.Dispose();
    }
}
