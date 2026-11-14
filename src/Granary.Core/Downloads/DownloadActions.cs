using Granary.Core.Abstractions;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// What a title's Download item shows: whether it is there, its words, and whether it opens the Downloads page (a download on
/// this PC) instead of the download dialog, and offers "Delete download" beside it.
/// </summary>
public sealed record TitleDownloadState(bool IsShown, string Label, bool OpensDownloads, bool CanDelete)
{
    public static TitleDownloadState Hidden { get; } = new(false, "", false, false);
}

/// <summary>
/// The title page's Download items, and the cards' (<see cref="CardDownloads"/>). "Download" is the web's overflow item
/// (<c>pages/ItemDetail/components/ActionBar.tsx</c>) and opens the download dialog while the server lets this profile
/// download; a title on this PC reads its status as the apps do ("Downloading · 42%", "Downloaded"), opens the Downloads page
/// and can be deleted after asking, whatever the server says now. One for the app; each title page follows the downloads
/// through <see cref="Follow"/> while it shows.
/// </summary>
public sealed class DownloadActions
{
    public const string DownloadLabel = "Download";
    public const string DownloadSeasonLabel = "Download season";
    public const string DeleteLabel = "Delete download";
    public const string DeleteTitle = "Delete download?";
    public const string DeleteMessage = "This removes the downloaded copy from this PC. Nothing is deleted from your Silo server.";
    public const string DeleteAction = "Delete";

    private readonly DownloadManager _downloads;
    private readonly DownloadsFeature _feature;
    private readonly IDownloadOptionsDialog _dialog;
    private readonly IConfirmation _confirmation;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<DownloadOptionsViewModel> _options;
    private readonly ILogger<DownloadActions> _logger;

    public DownloadActions(DownloadManager downloads, DownloadsFeature feature, IDownloadOptionsDialog dialog, IConfirmation confirmation,
        INavigationService navigation, IUiDispatcher dispatcher, Func<DownloadOptionsViewModel> options, ILogger<DownloadActions> logger)
    {
        _downloads = downloads;
        _feature = feature;
        _dialog = dialog;
        _confirmation = confirmation;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
    }

    /// <summary>The server has downloads on and lets the active profile download.</summary>
    public bool CanDownload => _feature.CanDownload;

    /// <summary>The Download item of a film or an episode, by its own id.</summary>
    public TitleDownloadState StateOf(string mediaItemId)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaItemId);
        if (_downloads.ForItem(mediaItemId) is { } download)
        {
            return new TitleDownloadState(true, DownloadText.Status(download), OpensDownloads: true, CanDelete: true);
        }
        return CanDownload ? new TitleDownloadState(true, DownloadLabel, OpensDownloads: false, CanDelete: false) : TitleDownloadState.Hidden;
    }

    /// <summary>
    /// Runs <paramref name="changed"/> on the UI thread whenever the active profile's downloads or what the server offers
    /// change, and once what it offers has been read; a burst of changes runs it once. A transfer that only went further runs
    /// it only when <paramref name="concerns"/> says its film or episode is one the page shows; that is asked on any thread.
    /// Disposing stops it.
    /// </summary>
    public IDisposable Follow(Func<string, bool> concerns, Action changed)
    {
        ArgumentNullException.ThrowIfNull(concerns);
        ArgumentNullException.ThrowIfNull(changed);
        var follower = new Follower(this, concerns, changed);
        _downloads.Changed += follower.OnChanged;
        _ = ReadOfferAsync(follower);
        return follower;
    }

    /// <summary>The download dialog for a film or an episode; it closes once the download started or the viewer cancels.</summary>
    public Task OfferTitleAsync(DownloadRequest request, IReadOnlyList<DownloadVersionRow> versions, int runtimeSeconds)
    {
        var options = _options();
        options.ForTitle(request, versions, runtimeSeconds);
        return _dialog.ShowAsync(options);
    }

    /// <summary>The download dialog for a season's episodes.</summary>
    public Task OfferSeasonAsync(SeasonDownloadRequest request, string seasonTitle, long knownBytes)
    {
        var options = _options();
        options.ForSeason(request, seasonTitle, knownBytes);
        return _dialog.ShowAsync(options);
    }

    /// <summary>
    /// The download dialog for a card's film, episode or season, whose files the card does not carry: it opens at once and
    /// reads what to offer while it shows (<paramref name="read"/>), and the read stops once it closes.
    /// </summary>
    public async Task OfferReadAsync(Func<CancellationToken, Task<DownloadOffer>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var options = _options();
        options.ForReading(read);
        try
        {
            await _dialog.ShowAsync(options);
        }
        finally
        {
            options.StopReading();
        }
    }

    /// <summary>
    /// Deletes the download of a film or an episode from this PC, after asking; the server hears it once the files are gone. It
    /// goes in the profile the question was asked in, whichever is active once it is answered.
    /// </summary>
    public async Task DeleteAsync(string mediaItemId)
    {
        if (_downloads.ActiveScope is not { } scope || _downloads.ForItem(mediaItemId) is not { } download
            || !await _confirmation.AskAsync(DeleteTitle, DeleteMessage, DeleteAction))
        {
            return;
        }
        await _downloads.DeleteAsync(scope, download.DownloadId, CancellationToken.None);
    }

    /// <summary>The Downloads page, as its sidebar entry opens it.</summary>
    public void OpenDownloads() => _navigation.ShowSection(AppRoute.Downloads);

    // The page shows Download once it knows the server offers it; the read is the feature's, kept per profile.
    private async Task ReadOfferAsync(Follower follower)
    {
        try
        {
            await _feature.RefreshAsync();
        }
        catch (Exception ex)
        {
            // The page's items only miss Download; they never take the page down.
            _logger.LogError("Reading whether the server offers downloads failed unexpectedly ({Failure})", DownloadLog.Failure(ex));
        }
        follower.OnChanged(this, EventArgs.Empty);
    }

    // The manager tells of changes on any thread; one show is posted at a time, and none after the page stopped following. Every
    // running transfer's progress comes twice a second: only the page's own downloads' is worth a show.
    private sealed class Follower(DownloadActions actions, Func<string, bool> concerns, Action changed) : IDisposable
    {
        private int _posted;
        private volatile bool _stopped;

        public void OnChanged(object? sender, EventArgs e)
        {
            if (_stopped || (e is DownloadProgressedEventArgs progressed && !Concerns(progressed.DownloadId))
                || Interlocked.Exchange(ref _posted, 1) == 1)
            {
                return;
            }
            actions._dispatcher.Post(() =>
            {
                Volatile.Write(ref _posted, 0);
                if (!_stopped)
                {
                    changed();
                }
            });
        }

        public void Dispose()
        {
            _stopped = true;
            actions._downloads.Changed -= OnChanged;
        }

        private bool Concerns(string downloadId) => actions._downloads.MediaItemIdOf(downloadId) is { } mediaItemId && concerns(mediaItemId);
    }
}
