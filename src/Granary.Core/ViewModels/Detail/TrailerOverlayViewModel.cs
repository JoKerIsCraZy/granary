using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Trailers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Core.ViewModels.Detail;

public enum TrailerOverlayState
{
    Closed,

    /// <summary>The player is on its way; it has <see cref="TrailerOverlayViewModel.LoadTimeout"/> to answer.</summary>
    Loading,

    /// <summary>YouTube's player is up and shows the trailer.</summary>
    Playing,

    /// <summary>It can't play here (<see cref="TrailerOverlayViewModel.Failure"/>); YouTube's page is offered instead.</summary>
    Unavailable,
}

/// <summary>
/// A trailer playing over the title's page, in YouTube's own embedded player (the App's TrailerOverlay holds the WebView2
/// and reports to this). Silo's apps hand a trailer to the YouTube app or the browser (Android's
/// <c>YoutubeTrailerLauncher</c>); Granary departs from them on purpose and plays it in the app, as the web's
/// <c>TrailerModal</c> does. Only through YouTube's player: never through mpv or a downloader, which YouTube's terms forbid.
/// </summary>
public sealed partial class TrailerOverlayViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the player has to answer before the trailer counts as one that can't play here.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    public const string UnavailableText = "This video can't play in Granary.";

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private ITimer? _timeout;
    private int _generation;

    public TrailerOverlayViewModel(IUiDispatcher dispatcher, TimeProvider time, ILogger<TrailerOverlayViewModel>? logger = null)
    {
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>A trailer opens: Granary's own player, minimized to its bar, pauses for it (MainWindow).</summary>
    public event EventHandler? Opened;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsLoading), nameof(IsPlaying), nameof(IsUnavailable))]
    public partial TrailerOverlayState State { get; private set; }

    [ObservableProperty]
    public partial TrailerFailure Failure { get; private set; }

    /// <summary>The trailer shown; null while closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Caption), nameof(WatchUrl))]
    public partial DetailTrailer? Trailer { get; private set; }

    public string Title => Trailer?.Title ?? "";

    public string Caption => Trailer?.Caption ?? "";

    /// <summary>The trailer's page on YouTube, for "Open on YouTube".</summary>
    public Uri? WatchUrl => Trailer?.WatchUrl;

    public bool IsOpen => State != TrailerOverlayState.Closed;

    public bool IsLoading => State == TrailerOverlayState.Loading;

    public bool IsPlaying => State == TrailerOverlayState.Playing;

    public bool IsUnavailable => State == TrailerOverlayState.Unavailable;

    /// <summary>Opens <paramref name="trailer"/>, in place of any shown.</summary>
    public void Open(DetailTrailer trailer)
    {
        ArgumentNullException.ThrowIfNull(trailer);
        StopTimeout();
        int generation = ++_generation;
        Failure = TrailerFailure.None;
        Trailer = trailer;
        State = TrailerOverlayState.Loading;
        _timeout = _time.CreateTimer(_ => _dispatcher.Post(() => TimeOut(generation)), null, LoadTimeout, Timeout.InfiniteTimeSpan);
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the page reported of the player: it answered, started, or failed.</summary>
    public void Receive(TrailerPageMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Event == TrailerPageEvent.Error)
        {
            Fail(PlayerFailure(message.ErrorCode));
            return;
        }
        if (State != TrailerOverlayState.Loading)
        {
            return;
        }
        StopTimeout();
        State = TrailerOverlayState.Playing;
        _logger.LogInformation("The trailer plays");
    }

    /// <summary>The trailer can't play here after all; only the first reason counts.</summary>
    public void Fail(TrailerFailure failure)
    {
        if (State is not (TrailerOverlayState.Loading or TrailerOverlayState.Playing) || failure == TrailerFailure.None)
        {
            return;
        }
        StopTimeout();
        Failure = failure;
        State = TrailerOverlayState.Unavailable;
        // The outcome only: never the video's key or address.
        _logger.LogInformation("The trailer can't play in Granary: {Failure}", failure);
    }

    /// <summary>Esc, the close button, Back, a click beside the trailer, or another page: the trailer stops.</summary>
    [RelayCommand]
    public void Close()
    {
        if (State == TrailerOverlayState.Closed)
        {
            return;
        }
        StopTimeout();
        _generation++;
        State = TrailerOverlayState.Closed;
        Trailer = null;
        Failure = TrailerFailure.None;
    }

    public void Dispose() => StopTimeout();

    // The IFrame Player API's onError codes.
    private static TrailerFailure PlayerFailure(int code) => code switch
    {
        100 => TrailerFailure.NotFound,
        101 or 150 => TrailerFailure.EmbedRefused,
        153 => TrailerFailure.Configuration,
        _ => TrailerFailure.PlayerError,
    };

    private void TimeOut(int generation)
    {
        if (generation == _generation && State == TrailerOverlayState.Loading)
        {
            Fail(TrailerFailure.TimedOut);
        }
    }

    private void StopTimeout()
    {
        _timeout?.Dispose();
        _timeout = null;
    }
}
