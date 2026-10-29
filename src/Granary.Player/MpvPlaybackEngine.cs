using System.Globalization;
using Granary.Core.Playback;
using Granary.Player.Interop;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

/// <summary>
/// <see cref="IPlaybackEngine"/> on libmpv. Video goes through mpv's d3d11 composition mode
/// (spike/README.md); a XAML host takes the swap chain via <see cref="ICompositionVideoOutput"/>.
/// </summary>
public sealed class MpvPlaybackEngine : IPlaybackEngine, ICompositionVideoOutput
{
    private static readonly TimeSpan PositionThrottle = TimeSpan.FromMilliseconds(250);

    private readonly MpvClient _mpv;
    private readonly ILogger<MpvPlaybackEngine> _logger;
    private readonly MpvEngineOptions _options;
    private readonly object _gate = new();
    private PlaybackSnapshot _current = PlaybackSnapshot.Idle;
    private TimeSpan? _lastReportedPosition;
    private long _loads;

    // The audio stream the latest load asked for; checked once mpv opened the file.
    private int? _loadAudio;

    private SubtitlePosition _subtitlePosition = SubtitlePosition.Bottom;
    private double _subtitleLift;

    // Set once the server refused a request of the current load. mpv then runs to the end of what it had read and
    // pauses there (keep-open), well before it says the file ended: positions and pauses from then on are not the
    // viewer's, so the snapshot keeps the last real ones until the next load, and the end is the refusal's.
    private bool _rejected;
    private (int Width, int Height) _outputSize;
    private nint _swapChain;

    public MpvPlaybackEngine(MpvEngineOptions options, ILogger<MpvPlaybackEngine> logger)
    {
        _options = options;
        _logger = logger;
        _outputSize = options.InitialOutputSize;
        _mpv = new MpvClient(logger);
        _mpv.PropertyChanged += OnPropertyChanged;
        _mpv.LogMessage += OnLogMessage;
        _mpv.FileEnded += OnFileEnded;
        _mpv.LifecycleEvent += OnLifecycleEvent;
        Configure();
        _mpv.Initialize(
        [
            ("time-pos", LibMpv.FormatDouble),
            ("duration", LibMpv.FormatDouble),
            ("pause", LibMpv.FormatFlag),
            ("eof-reached", LibMpv.FormatFlag),
            ("hwdec-current", LibMpv.FormatString),
            ("paused-for-cache", LibMpv.FormatFlag),
            ("volume", LibMpv.FormatDouble),
            ("mute", LibMpv.FormatFlag),
        ]);
        _logger.LogInformation("libmpv {Version} initialized ({Output})", _mpv.GetString("mpv-version"), options.VideoOutput);
        RefreshSwapChain();
    }

    public PlaybackSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public nint SwapChain => _swapChain;

    public event EventHandler<PlaybackSnapshot>? StateChanged;

    public event EventHandler<PlaybackEnded>? Ended;

    public event EventHandler? SwapChainChanged;

    private void Configure()
    {
        _mpv.SetOption("hwdec", _options.HardwareDecoding);
        _mpv.SetOption("keep-open", "yes");
        _mpv.SetOption("idle", "yes");
        _mpv.SetOption("input-default-bindings", "no");
        _mpv.SetOption("input-vo-keyboard", "no");
        _mpv.SetOption("osc", "no");
        _mpv.SetOption("ytdl", "no");
        _mpv.SetOption("sub-auto", "no");
        _mpv.SetOption("rebase-start-time", "yes");
        // Granary drives System Media Transport Controls itself.
        _mpv.SetOption("media-controls", "no");
        // The server keeps the position. mpv must not read or write watch-later files, which would store the URLs,
        // the media proxy's secret among them.
        _mpv.SetOption("resume-playback", "no");
        _mpv.SetOption("save-position-on-quit", "no");

        switch (_options.VideoOutput)
        {
            case VideoOutputKind.D3D11Composition:
                _mpv.SetOption("vo", "gpu-next");
                _mpv.SetOption("gpu-api", "d3d11");
                _mpv.SetOption("gpu-context", "d3d11");
                _mpv.SetOption("d3d11-output-mode", "composition");
                _mpv.SetOption("d3d11-composition-size", SizeText(_outputSize));
                _mpv.SetOption("force-window", "yes");
                break;
            case VideoOutputKind.None:
                _mpv.SetOption("vo", "null");
                _mpv.SetOption("ao", "null");
                break;
        }
    }

    public long Load(MediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var (name, value) in source.Headers)
        {
            if (!IsSafeHeader(name, value))
            {
                throw new ArgumentException($"Header '{name}' cannot be passed to mpv safely.", nameof(source));
            }
        }
        string headers = string.Join(",", source.Headers.Select(h => $"{h.Key}: {h.Value}"));
        _mpv.SetProperty("http-header-fields", headers);

        // Plan URLs are replayed byte for byte (docs/api-contract.md, 9.2): the string as given, not as Uri re-escapes it.
        string target = source.Url.IsFile ? source.Url.LocalPath : source.Url.OriginalString;
        string perFileOptions = string.Join(",", PerFileOptions(source));
        long loadId;
        lock (_gate)
        {
            loadId = ++_loads;
            _loadAudio = source.AudioStream;
            _lastReportedPosition = null;
            _rejected = false;
        }
        // The file before is still loaded until mpv replaces it: nothing of it may read as the new one's start.
        Update(s => s with { LoadId = loadId, IsLoaded = false, HasStarted = false, IsAtEnd = false }, force: true);
        // The values, not the option string: whatever is added to it later never reaches the log by accident.
        _logger.LogInformation("Loading {Target} with {HeaderCount} header(s) at {Start} s, audio stream {Audio}, subtitle stream {Subtitle}",
            Describe(source.Url), source.Headers.Count, source.Start?.TotalSeconds ?? 0, source.AudioStream?.ToString(CultureInfo.InvariantCulture) ?? "auto",
            source.SubtitleStream?.ToString(CultureInfo.InvariantCulture) ?? "auto");
        _mpv.Command("loadfile", target, "replace", "-1", perFileOptions);
        return loadId;
    }

    // Options for this file only: the next load starts from mpv's defaults again. The audio stream is always named:
    // a switch during a file set without one (SetAudioStream) would otherwise carry over to the next.
    internal static IEnumerable<string> PerFileOptions(MediaSource source)
    {
        if (source.Start is { } start)
        {
            yield return "start=" + start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        }
        yield return "aid=" + (source.AudioStream is { } audio ? audio.ToString(CultureInfo.InvariantCulture) : "auto");
        if (source.SubtitleStream is { } subtitle)
        {
            yield return "sid=" + (subtitle > 0 ? subtitle.ToString(CultureInfo.InvariantCulture) : "no");
        }
    }

    /// <summary>
    /// mpv reads <c>http-header-fields</c> as a list: commas split it, a backslash escapes, and an item starting with a
    /// quote, a bracket or <c>%n%</c> is read as a quoted or counted string. Names must be HTTP tokens and values
    /// printable ASCII without any of these, so no header can split, merge or smuggle another.
    /// </summary>
    internal static bool IsSafeHeader(string name, string value) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && value.All(c => c is >= ' ' and <= '~' and not ',' and not '\\')
        && (value.Length == 0 || value[0] is not ('"' or '\'' or '[' or '{' or '%'));

    public void SetPaused(bool paused) => _mpv.SetProperty("pause", paused ? "yes" : "no");

    public void Seek(TimeSpan position) =>
        _mpv.Command("seek", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");

    public void SetVolume(double percent) =>
        _mpv.SetProperty("volume", Math.Clamp(percent, 0, 100).ToString("0.##", CultureInfo.InvariantCulture));

    public void SetMuted(bool muted) => _mpv.SetProperty("mute", muted ? "yes" : "no");

    // A global option: it stays for the next file. mpv keeps the pitch (audio-pitch-correction).
    public void SetSpeed(double speed) =>
        _mpv.SetProperty("speed", Math.Clamp(speed, 0.25, 4).ToString("0.##", CultureInfo.InvariantCulture));

    // panscan 1 scales the picture up until it fills the area on both sides, as object-fit: cover does on the web.
    public void SetFill(bool fill) => _mpv.SetProperty("panscan", fill ? "1" : "0");

    // width and height are the picture as decoded, the drops counted since the file was loaded.
    public PlaybackStats ReadStats() => new(
        (int)(_mpv.GetInt64("width") ?? 0),
        (int)(_mpv.GetInt64("height") ?? 0),
        _mpv.GetInt64("frame-drop-count") ?? -1,
        _mpv.GetInt64("decoder-frame-drop-count") ?? -1,
        _mpv.GetString("hwdec-current"));

    // mpv takes any id and plays on without sound when the file lacks it: the switch took once that stream plays,
    // and one that did not puts the track before back.
    public bool SetAudioStream(int stream)
    {
        if (stream <= 0)
        {
            return false;
        }
        string id = stream.ToString(CultureInfo.InvariantCulture);
        string previous = _mpv.GetString("aid") ?? "auto";
        if (_mpv.SetProperty("aid", id) >= 0 && _mpv.GetString("current-tracks/audio/id") == id)
        {
            return true;
        }
        _mpv.SetProperty("aid", previous);
        return false;
    }

    /// <summary>The audio stream that plays, counted from 1; null without one.</summary>
    internal string? PlayingAudioStream => _mpv.GetString("current-tracks/audio/id");

    // sub-add reads the whole file before it returns; "auto" adds it without selecting it, so a file that arrives after
    // another load cannot take over that one's subtitles. Its stream number is read back from the track list.
    public Task<int?> AddSubtitleAsync(Uri url, long loadId)
    {
        ArgumentNullException.ThrowIfNull(url);
        string target = url.IsFile ? url.LocalPath : url.OriginalString;
        return Task.Run(() =>
        {
            if (!IsCurrentLoad(loadId) || _mpv.Command("sub-add", target, "auto") < 0 || !IsCurrentLoad(loadId))
            {
                return null;
            }
            int? stream = null;
            long count = _mpv.GetInt64("track-list/count") ?? 0;
            for (long i = 0; i < count; i++)
            {
                if (_mpv.GetString($"track-list/{i}/type") == "sub" && _mpv.GetString($"track-list/{i}/external-filename") == target)
                {
                    stream = (int?)_mpv.GetInt64($"track-list/{i}/id");
                }
            }
            return stream;
        });
    }

    public void RemoveSubtitle(int stream)
    {
        if (stream > 0)
        {
            _mpv.Command("sub-remove", stream.ToString(CultureInfo.InvariantCulture));
        }
    }

    public int? SubtitleStreamOf(int ffmpegIndex)
    {
        long count = _mpv.GetInt64("track-list/count") ?? 0;
        for (long i = 0; i < count; i++)
        {
            if (_mpv.GetString($"track-list/{i}/type") == "sub" && _mpv.GetInt64($"track-list/{i}/ff-index") == ffmpegIndex
                && _mpv.GetString($"track-list/{i}/external") != "yes")
            {
                return (int?)_mpv.GetInt64($"track-list/{i}/id");
            }
        }
        return null;
    }

    public bool SetSubtitleStream(int stream)
    {
        if (stream <= 0)
        {
            return _mpv.SetProperty("sid", "no") >= 0;
        }
        string id = stream.ToString(CultureInfo.InvariantCulture);
        return _mpv.SetProperty("sid", id) >= 0 && _mpv.GetString("current-tracks/sub/id") == id;
    }

    public void SetSubtitleDelay(double seconds) =>
        _mpv.SetProperty("sub-delay", seconds.ToString("0.###", CultureInfo.InvariantCulture));

    public void SetSubtitleLook(SubtitleAppearance look)
    {
        ArgumentNullException.ThrowIfNull(look);
        foreach (var (name, value) in SubtitleOptions(look))
        {
            _mpv.SetProperty(name, value);
        }
        lock (_gate)
        {
            _subtitlePosition = look.Position;
        }
        ApplySubtitlePosition();
    }

    public void SetSubtitleLift(double fraction)
    {
        lock (_gate)
        {
            _subtitleLift = Math.Clamp(fraction, 0, 0.5);
        }
        ApplySubtitlePosition();
    }

    // The clients' look in libass terms (Android's caption style, Apple's render style): sizes are scaled pixels at a
    // window height of 720 where the clients count at 1080 lines; shadow and outline take the outline colour; a box
    // gets an eighth of the text size as its margin, as Android pads it.
    internal static IEnumerable<(string Name, string Value)> SubtitleOptions(SubtitleAppearance look)
    {
        const double Scale = 720.0 / 1080;
        const double Outline = 1.65;
        double size = look.FontSize * Scale;
        yield return ("sub-font-size", Number(size));
        yield return ("sub-font", look.FontFamily switch
        {
            SubtitleAppearance.Serif => "Times New Roman",
            SubtitleAppearance.Monospace => "Consolas",
            var family => family,
        });
        yield return ("sub-color", Color(look.FontColor, look.TextOpacity));
        yield return ("sub-outline-color", Color(look.TextOutlineColor, 100));
        double outline = look.TextOutline ? Outline : 0;
        switch (look.Background)
        {
            case SubtitleBackground.Box:
                yield return ("sub-border-style", "background-box");
                yield return ("sub-back-color", Color(look.BackgroundColor, look.BackgroundOpacity));
                yield return ("sub-shadow-offset", Number(size / 8));
                break;
            case SubtitleBackground.Shadow:
                yield return ("sub-border-style", "outline-and-shadow");
                yield return ("sub-back-color", Color(look.TextOutlineColor, 100));
                yield return ("sub-shadow-offset", "2");
                break;
            case SubtitleBackground.Outline:
                yield return ("sub-border-style", "outline-and-shadow");
                yield return ("sub-shadow-offset", "0");
                outline = Outline;
                break;
            default:
                yield return ("sub-border-style", "outline-and-shadow");
                yield return ("sub-shadow-offset", "0");
                break;
        }
        yield return ("sub-outline-size", Number(outline));
        yield return ("sub-align-y", look.Position == SubtitlePosition.Top ? "top" : "bottom");

        static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        // #AARRGGBB, the alpha from a percentage.
        static string Color(string rgb, int opacity) =>
            "#" + ((int)Math.Round(Math.Clamp(opacity, 0, 100) * 255 / 100.0)).ToString("X2", CultureInfo.InvariantCulture) + rgb.TrimStart('#').ToUpperInvariant();
    }

    // sub-pos counts in percent of the picture's height from the top, 100 being the usual bottom place. Lower third
    // sits at 70 (Android's position for it); top aligns to the top edge, which a lift leaves alone. The lift moves
    // every kind of subtitle, styled ASS and images too.
    private void ApplySubtitlePosition()
    {
        SubtitlePosition position;
        double lift;
        lock (_gate)
        {
            position = _subtitlePosition;
            lift = _subtitleLift;
        }
        double pos = position switch
        {
            SubtitlePosition.LowerThird => 70 - lift * 100,
            SubtitlePosition.Top => 100,
            _ => 100 - lift * 100,
        };
        _mpv.SetProperty("sub-pos", Math.Max(0, pos).ToString("0.#", CultureInfo.InvariantCulture));
    }

    private bool IsCurrentLoad(long loadId)
    {
        lock (_gate)
        {
            return _loads == loadId;
        }
    }

    public void Stop() => _mpv.Command("stop");

    public void Resize(int widthPixels, int heightPixels)
    {
        if (_options.VideoOutput != VideoOutputKind.D3D11Composition || widthPixels < 1 || heightPixels < 1)
        {
            return;
        }
        var size = (widthPixels, heightPixels);
        if (size == _outputSize)
        {
            return;
        }
        _outputSize = size;
        _mpv.SetProperty("d3d11-composition-size", SizeText(size));
        RefreshSwapChain();
    }

    private void RefreshSwapChain()
    {
        if (_options.VideoOutput != VideoOutputKind.D3D11Composition)
        {
            return;
        }
        nint pointer = (nint)(_mpv.GetInt64("display-swapchain") ?? 0);
        if (pointer != 0 && pointer != _swapChain)
        {
            _swapChain = pointer;
            _logger.LogInformation("Composition swap chain ready ({Width}x{Height})", _outputSize.Width, _outputSize.Height);
            SwapChainChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnLifecycleEvent(int eventId)
    {
        if (eventId is LibMpv.EventVideoReconfig or LibMpv.EventFileLoaded or LibMpv.EventPlaybackRestart)
        {
            RefreshSwapChain();
        }
        if (eventId == LibMpv.EventFileLoaded)
        {
            KeepLoadedAudio();
            Update(s => s with { IsLoaded = true }, force: true);
        }
        // mpv restarts playback after the load and after every seek; the first one is the start.
        if (eventId == LibMpv.EventPlaybackRestart && !Current.HasStarted)
        {
            Update(s => s with { HasStarted = true }, force: true);
        }
    }

    // mpv was seen to play a server stream's default audio track although the load named another one (aid), now and
    // then after other playbacks in the same run; it could not be reproduced here. The track is checked once the file
    // is open, before the first sound, and selected again if needed.
    private void KeepLoadedAudio()
    {
        int? wanted;
        lock (_gate)
        {
            wanted = _loadAudio;
        }
        if (wanted is not { } stream)
        {
            return;
        }
        string id = stream.ToString(CultureInfo.InvariantCulture);
        string? playing = _mpv.GetString("current-tracks/audio/id");
        if (playing != id)
        {
            _logger.LogWarning("mpv opened the file with audio stream {Playing} instead of {Wanted}; selecting it", playing ?? "none", id);
            _mpv.SetProperty("aid", id);
        }
    }

    private void OnPropertyChanged(string name, object? value)
    {
        switch (name)
        {
            case "time-pos" when !IsRejected:
                Update(s => s with { Position = TimeSpan.FromSeconds(value as double? ?? 0) }, force: false);
                break;
            case "duration":
                Update(s => s with { Duration = value is double d ? TimeSpan.FromSeconds(d) : null }, force: true);
                break;
            case "pause" when !IsRejected:
                Update(s => s with { IsPaused = value as bool? ?? false }, force: true);
                break;
            case "hwdec-current":
                Update(s => s with { HardwareDecoder = value as string }, force: true);
                break;
            case "paused-for-cache":
                Update(s => s with { IsBuffering = value as bool? ?? false }, force: true);
                break;
            case "volume" when value is double volume:
                Update(s => s with { Volume = volume }, force: true);
                break;
            case "mute":
                Update(s => s with { IsMuted = value as bool? ?? false }, force: true);
                break;
            case "eof-reached" when value is true:
                // keep-open=yes pauses at the end instead of ending the file.
                var atEnd = Update(s => s with { IsAtEnd = true }, force: true);
                Ended?.Invoke(this, new PlaybackEnded(
                    IsRejected ? PlaybackEndReason.AuthenticationFailed : PlaybackEndReason.EndOfFile, null, atEnd.LoadId));
                break;
            case "eof-reached":
                Update(s => s with { IsAtEnd = false }, force: true);
                break;
        }
    }

    private void OnFileEnded(int reason, int error)
    {
        // mpv keeps its pause, volume and mute across files, and says nothing of them for the next one.
        var idle = Update(s => PlaybackSnapshot.Idle with { IsPaused = s.IsPaused, Volume = s.Volume, IsMuted = s.IsMuted, LoadId = s.LoadId }, force: true);
        var ended = reason switch
        {
            LibMpv.EndFileError when IsRejected => new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, LibMpv.Error(error)),
            LibMpv.EndFileError => new PlaybackEnded(PlaybackEndReason.Error, LibMpv.Error(error)),
            LibMpv.EndFileEof => new PlaybackEnded(PlaybackEndReason.EndOfFile, null),
            _ => new PlaybackEnded(PlaybackEndReason.Stopped, null),
        };
        Ended?.Invoke(this, ended with { LoadId = idle.LoadId });
    }

    private void OnLogMessage(string prefix, string level, string text)
    {
        var failure = MpvLogClassifier.Classify(prefix, text);
        if (failure is MpvNetworkFailure.Unauthorized or MpvNetworkFailure.Forbidden)
        {
            lock (_gate)
            {
                _rejected = true;
            }
        }
        string safe = MpvLogClassifier.Redact(text);
        var logLevel = level switch
        {
            _ when IsExpectedCompositionNoise(prefix, text) => LogLevel.Debug,
            "fatal" or "error" => LogLevel.Error,
            "warn" => LogLevel.Warning,
            "info" => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        _logger.Log(logLevel, "mpv {Prefix}: {Text}", prefix, safe);
    }

    // In composition mode there is no window, so mpv cannot query the output and says so at error level.
    private bool IsExpectedCompositionNoise(string prefix, string text) =>
        _options.VideoOutput == VideoOutputKind.D3D11Composition
        && prefix.EndsWith("/d3d11", StringComparison.Ordinal)
        && text.StartsWith("Failed to query swap chain's output information", StringComparison.Ordinal);

    private bool IsRejected
    {
        get
        {
            lock (_gate)
            {
                return _rejected;
            }
        }
    }

    private PlaybackSnapshot Update(Func<PlaybackSnapshot, PlaybackSnapshot> change, bool force)
    {
        PlaybackSnapshot next;
        lock (_gate)
        {
            next = change(_current);
            _current = next;
            if (!force && _lastReportedPosition is { } last && (next.Position - last).Duration() < PositionThrottle)
            {
                return next;
            }
            _lastReportedPosition = next.Position;
        }
        StateChanged?.Invoke(this, next);
        return next;
    }

    private static string SizeText((int Width, int Height) size) =>
        string.Create(CultureInfo.InvariantCulture, $"{size.Width}x{size.Height}");

    private static string Describe(Uri url) =>
        url.IsFile ? "local file" : url.GetLeftPart(UriPartial.Path);

    public void Dispose()
    {
        _mpv.PropertyChanged -= OnPropertyChanged;
        _mpv.LogMessage -= OnLogMessage;
        _mpv.FileEnded -= OnFileEnded;
        _mpv.LifecycleEvent -= OnLifecycleEvent;
        _mpv.Dispose();
    }
}
