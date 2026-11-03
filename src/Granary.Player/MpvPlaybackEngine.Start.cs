using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Granary.Core.Playback;
using Granary.Player.Interop;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

// How mpv starts, with the viewer's own mpv.conf or without (docs/architecture.md, "Your own mpv.conf").
//
// Without a file, mpv starts as it always did: Granary's core options, then mpv_initialize. With a mpv.conf in the config
// folder, mpv reads it itself in mpv_initialize (config, config-dir; ~~/ is that folder), after everything set before, so
// the file could override Granary's options, and mpv acts on some of them as soon as mpv_initialize is done, before any
// call after it could set them back: it makes its video output and leaves idle mode on what the file says. So Granary's
// options also go into the pseudo-GUI profile, which mpv applies over the config files inside mpv_initialize. A file
// that would turn that profile off, or override it, is refused, before the start and again after it with what mpv read.
// Each title gets Granary's options as options of its own (Load), over the file's protocol and extension profiles, and a
// profile applied while mpv runs is set back as mpv reports it (KeepsOwn), a few times at most.
public sealed partial class MpvPlaybackEngine
{
    // A profile that keeps setting one of Granary's options back (its condition follows the option) wins after this many.
    private const int SetBackLimit = 5;

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

    private readonly IReadOnlyList<KeyValuePair<string, string>> _core;
    private MpvClient _mpv;

    // With a mpv.conf: every option Granary keeps (its core ones and the guarded ones this mpv knows), those it keeps
    // while mpv runs, by name, and the options each title gets. Empty or null without one.
    private IReadOnlyList<KeyValuePair<string, string>> _own = [];
    private Dictionary<string, string>? _kept;
    private string[] _perFile = [];

    // How often each option was set back; on mpv's event thread only.
    private readonly Dictionary<string, int> _setBacks = [];

    [MemberNotNull(nameof(_mpv))]
    private void Start()
    {
        if (ConfigFile(_options.ConfigDirectory) is { } file && StartWith(file))
        {
            return;
        }
        Prepare(null);
        Launch(null);
    }

    private string? ConfigFile(string? directory)
    {
        if (directory is null)
        {
            return null;
        }
        string file = Path.Combine(directory, MpvConfigFolder.ConfigFileName);
        if (Directory.Exists(file))
        {
            _logger.LogWarning("mpv.conf in {Folder} is a folder, not a file; mpv plays without it", directory);
            return null;
        }
        return File.Exists(file) ? file : null;
    }

    // True when mpv runs with the file; false, with nothing open, when it is to play without it.
    [MemberNotNullWhen(true, nameof(_mpv))]
    private bool StartWith(string file)
    {
        var all = _core.Concat(MpvOwnOptions.Guarded(_options.VideoOutput)).ToList();
        var names = all.Select(o => o.Key).Append(MpvOwnOptions.OperationMode).ToHashSet();
        var entries = Read(file) is { } text ? MpvConfigText.Entries(text, names) : [];
        if (MpvConfigText.Refusal(entries, Kept(all)) is { } reason)
        {
            _logger.LogWarning("mpv.conf {Reason}; mpv plays without it", reason);
            return false;
        }
        try
        {
            Prepare(file);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("mpv did not take Granary's own options for mpv.conf ({Reason}); it plays without the file", ex.Message);
            return false;
        }
        try
        {
            Launch(file);
        }
        catch (InvalidOperationException ex)
        {
            // An option that only lists the audio devices, say. mpv has said why.
            _logger.LogWarning("mpv refused to start with {File} ({Reason}); it plays without it", file, ex.Message);
            return false;
        }
        if (RefusalAfterStart(names) is { } late)
        {
            // An include or a profile the file applied while mpv started did what the file itself would have been refused for.
            _logger.LogWarning("mpv.conf {Reason}; mpv starts again without it", late);
            Close();
            return false;
        }
        SayWhatIsKept(file, entries);
        return true;
    }

    private string? Read(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // mpv reads it again and says what it makes of it.
            _logger.LogDebug(ex, "mpv.conf could not be read to check it");
            return null;
        }
    }

    private static Dictionary<string, string> Kept(IEnumerable<KeyValuePair<string, string>> own) =>
        own.Where(o => o.Key != MpvOwnOptions.CompositionSize).ToDictionary(StringComparer.Ordinal);

    // A new mpv with Granary's options, and the file's when there is one; not started yet. Throws when mpv refuses one.
    [MemberNotNull(nameof(_mpv))]
    private void Prepare(string? configFile)
    {
        _own = [];
        _kept = null;
        _perFile = [];
        _setBacks.Clear();
        _mpv = new MpvClient(_logger);
        _mpv.PropertyChanged += OnPropertyChanged;
        _mpv.LogMessage += OnLogMessage;
        _mpv.FileEnded += OnFileEnded;
        _mpv.LifecycleEvent += OnLifecycleEvent;
        try
        {
            Configure(configFile);
        }
        catch
        {
            Close();
            throw;
        }
    }

    private void Launch(string? configFile)
    {
        try
        {
            _mpv.Initialize(_kept is null ? Followed : [.. Followed, .. _kept.Keys.Select(name => (name, LibMpv.FormatString))],
                logFromStart: configFile is not null);
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
        foreach (var (name, value) in _core)
        {
            _mpv.SetOption(name, value);
        }
        if (configFile is null)
        {
            return;
        }
        var own = new List<KeyValuePair<string, string>>(_core);
        foreach (var option in MpvOwnOptions.Guarded(_options.VideoOutput))
        {
            if (TrySet(option.Key, option.Value))
            {
                own.Add(option);
            }
        }
        string folder = Path.GetDirectoryName(configFile)!;
        _mpv.SetOption("config", "yes");
        _mpv.SetOption("config-dir", folder);
        // Compiled shaders are kept in the folder too, not in mpv's own under %LOCALAPPDATA%\mpv. The file may say otherwise.
        string cache = Path.Combine(folder, "cache");
        TrySet("gpu-shader-cache-dir", cache);
        TrySet("icc-cache-dir", cache);
        _mpv.LoadConfigText(MpvOwnOptions.Profile(own));
        _mpv.SetOption(MpvOwnOptions.OperationMode, MpvOwnOptions.ProfileName);
        _own = own;
        _kept = Kept(own);
        _perFile = [.. MpvOwnOptions.PerFile(own)];
    }

    // An option a newer mpv may have dropped or renamed: without it, mpv still starts.
    private bool TrySet(string name, string value)
    {
        int rc = _mpv.TrySetOption(name, value);
        if (rc < 0)
        {
            _logger.LogWarning("mpv does not take {Option} ({Error}); Granary cannot keep it", name, LibMpv.Error(rc));
        }
        return rc >= 0;
    }

    // What mpv read, includes too: the pseudo-GUI mode still on, and its profiles as the file could not have them.
    private string? RefusalAfterStart(IReadOnlySet<string> names)
    {
        IEnumerable<MpvConfigEntry> entries = _mpv.GetString(MpvOwnOptions.OperationMode) is { } mode
            ? [new(MpvOwnOptions.OperationMode, null, mode)]
            : [];
        try
        {
            entries = entries.Concat(MpvConfigText.FromProfiles(_mpv.GetString("profile-list"), _own, names));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _logger.LogDebug(ex, "mpv's profiles could not be read to check them");
        }
        return MpvConfigText.Refusal(entries, _kept ?? []);
    }

    // Once per start: the file mpv read, and the options of Granary's it sets, which the profile has already set back.
    private void SayWhatIsKept(string configFile, IReadOnlyList<MpvConfigEntry> entries)
    {
        _logger.LogInformation("mpv started with {File}", configFile);
        foreach (var (option, profile) in entries
            .Where(e => e.Option == MpvOwnOptions.CompositionSize || _kept!.ContainsKey(e.Option))
            .Select(e => (e.Option, e.Profile)).Distinct())
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

    // A notice of one of Granary's options while mpv runs with a mpv.conf: a profile of the file set it (profile-cond, or
    // one applied by name). It is set back, at most SetBackLimit times. True for these names.
    private bool KeepsOwn(string name, object? value)
    {
        if (_kept is not { } kept || !kept.TryGetValue(name, out string? own))
        {
            return false;
        }
        if (value is not string current || MpvOwnOptions.Same(current, own))
        {
            return true;
        }
        int times = _setBacks.GetValueOrDefault(name);
        // A notice can arrive after the value changed back already.
        if (times > SetBackLimit || MpvOwnOptions.Same(_mpv.GetString(name), own))
        {
            return true;
        }
        _setBacks[name] = ++times;
        if (times > SetBackLimit)
        {
            _logger.LogWarning("mpv.conf keeps setting {Option}; Granary stops setting it back", name);
            return true;
        }
        if (times == 1)
        {
            _logger.LogWarning("mpv.conf set {Option}; Granary keeps its own", name);
        }
        int rc = _mpv.SetProperty(name, own);
        if (rc < 0 && times == 1)
        {
            _logger.LogWarning("mpv did not take Granary's {Option} back: {Error}", name, LibMpv.Error(rc));
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
