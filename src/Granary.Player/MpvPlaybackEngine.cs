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
    // A 401 this close to an end of file or load error explains it (spike: mid-stream 401 = EOF).
    private static readonly TimeSpan AuthFailureWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PositionThrottle = TimeSpan.FromMilliseconds(250);

    private readonly MpvClient _mpv;
    private readonly ILogger<MpvPlaybackEngine> _logger;
    private readonly MpvEngineOptions _options;
    private readonly object _gate = new();
    private PlaybackSnapshot _current = PlaybackSnapshot.Idle;
    private TimeSpan? _lastReportedPosition;
    private long _lastAuthFailureTick = long.MinValue;

    // Set once the server refused a request of the current load. mpv then runs to the end of what it had read and
    // pauses there (keep-open), well before it says the file ended: positions and pauses from then on are not the
    // viewer's, so the snapshot keeps the last real ones until the next load.
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

    public void Load(MediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // mpv parses http-header-fields as a comma-separated list; reject values that would split.
        foreach (var (name, value) in source.Headers)
        {
            if (name.AsSpan().IndexOfAny(",\r\n:") >= 0 || value.AsSpan().IndexOfAny(",\r\n") >= 0)
            {
                throw new ArgumentException($"Header '{name}' cannot be passed to mpv safely.", nameof(source));
            }
        }
        string headers = string.Join(",", source.Headers.Select(h => $"{h.Key}: {h.Value}"));
        _mpv.SetProperty("http-header-fields", headers);

        // Plan URLs are replayed byte for byte (docs/api-contract.md, 9.2): the string as given, not as Uri re-escapes it.
        string target = source.Url.IsFile ? source.Url.LocalPath : source.Url.OriginalString;
        string perFileOptions = source.Start is { } start
            ? $"start={start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}"
            : "";
        lock (_gate)
        {
            _lastAuthFailureTick = long.MinValue;
            _lastReportedPosition = null;
            _rejected = false;
        }
        Update(s => s with { HasStarted = false, IsAtEnd = false }, force: true);
        _logger.LogInformation("Loading {Target} with {HeaderCount} header(s)", Describe(source.Url), source.Headers.Count);
        _mpv.Command("loadfile", target, "replace", "-1", perFileOptions);
    }

    public void SetPaused(bool paused) => _mpv.SetProperty("pause", paused ? "yes" : "no");

    public void Seek(TimeSpan position) =>
        _mpv.Command("seek", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");

    public void SetVolume(double percent) =>
        _mpv.SetProperty("volume", Math.Clamp(percent, 0, 100).ToString("0.##", CultureInfo.InvariantCulture));

    public void SetMuted(bool muted) => _mpv.SetProperty("mute", muted ? "yes" : "no");

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
            Update(s => s with { IsLoaded = true }, force: true);
        }
        // mpv restarts playback after the load and after every seek; the first one is the start.
        if (eventId == LibMpv.EventPlaybackRestart && !Current.HasStarted)
        {
            Update(s => s with { HasStarted = true }, force: true);
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
                Update(s => s with { IsAtEnd = true }, force: true);
                Ended?.Invoke(this, new PlaybackEnded(
                    RecentAuthFailure() ? PlaybackEndReason.AuthenticationFailed : PlaybackEndReason.EndOfFile, null));
                break;
            case "eof-reached":
                Update(s => s with { IsAtEnd = false }, force: true);
                break;
        }
    }

    private void OnFileEnded(int reason, int error)
    {
        Update(s => PlaybackSnapshot.Idle with { Volume = s.Volume, IsMuted = s.IsMuted }, force: true);
        var ended = reason switch
        {
            LibMpv.EndFileError when RecentAuthFailure() => new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, LibMpv.Error(error)),
            LibMpv.EndFileError => new PlaybackEnded(PlaybackEndReason.Error, LibMpv.Error(error)),
            LibMpv.EndFileEof => new PlaybackEnded(PlaybackEndReason.EndOfFile, null),
            _ => new PlaybackEnded(PlaybackEndReason.Stopped, null),
        };
        Ended?.Invoke(this, ended);
    }

    private void OnLogMessage(string prefix, string level, string text)
    {
        var failure = MpvLogClassifier.Classify(prefix, text);
        if (failure is MpvNetworkFailure.Unauthorized or MpvNetworkFailure.Forbidden)
        {
            lock (_gate)
            {
                _lastAuthFailureTick = Environment.TickCount64;
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

    private bool RecentAuthFailure()
    {
        lock (_gate)
        {
            return _lastAuthFailureTick != long.MinValue
                && Environment.TickCount64 - _lastAuthFailureTick <= (long)AuthFailureWindow.TotalMilliseconds;
        }
    }

    private void Update(Func<PlaybackSnapshot, PlaybackSnapshot> change, bool force)
    {
        PlaybackSnapshot next;
        lock (_gate)
        {
            next = change(_current);
            _current = next;
            if (!force && _lastReportedPosition is { } last && (next.Position - last).Duration() < PositionThrottle)
            {
                return;
            }
            _lastReportedPosition = next.Position;
        }
        StateChanged?.Invoke(this, next);
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
