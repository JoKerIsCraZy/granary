using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

/// <summary>
/// Transport state and commands for the player page. Phase 2 opens a local file or URL directly;
/// Phase 3 replaces that with the server's playback plan.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly IUiDispatcher _dispatcher;

    public PlayerViewModel(IPlaybackEngine engine, IUiDispatcher dispatcher)
    {
        _engine = engine;
        _dispatcher = dispatcher;
        _engine.StateChanged += OnStateChanged;
        _engine.Ended += OnEnded;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial string Source { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        string text = Source.Trim().Trim('"');
        var source = Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? new MediaSource(uri, [])
            : MediaSource.Local(text);
        Status = "Loading…";
        _engine.Load(source);
    }

    private bool CanOpen() => !string.IsNullOrWhiteSpace(Source);

    [RelayCommand]
    private void TogglePause() => _engine.SetPaused(!_engine.Current.IsPaused);

    /// <summary>Called when the user moves the seek bar (not for programmatic updates).</summary>
    public void SeekTo(double seconds) => _engine.Seek(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    private void OnStateChanged(object? sender, PlaybackSnapshot state) => _dispatcher.Post(() =>
    {
        IsPaused = state.IsPaused;
        PositionSeconds = state.Position.TotalSeconds;
        DurationSeconds = state.Duration?.TotalSeconds ?? 0;
        if (state.IsLoaded)
        {
            Status = state.HardwareDecoder is { Length: > 0 } decoder && decoder != "no" ? $"Hardware decoding: {decoder}" : "Software decoding";
        }
    });

    private void OnEnded(object? sender, PlaybackEnded ended) => _dispatcher.Post(() =>
    {
        Status = ended.Reason switch
        {
            PlaybackEndReason.EndOfFile => "Finished.",
            PlaybackEndReason.AuthenticationFailed => "The server rejected the credentials.",
            PlaybackEndReason.Error => $"Playback failed: {ended.Detail}",
            _ => Status,
        };
    });

    public void Dispose()
    {
        _engine.StateChanged -= OnStateChanged;
        _engine.Ended -= OnEnded;
    }
}
