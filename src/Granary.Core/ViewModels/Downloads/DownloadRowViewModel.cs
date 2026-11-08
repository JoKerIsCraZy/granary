using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// A row of the Downloads page: a film, or an episode under its series and season, with its size and its status in the apps'
/// words, a bar while it downloads, and Pause, Resume, Retry and Delete; Delete asks the title page's question
/// (<see cref="DownloadActions.DeleteTitle"/>). The page keeps one row per download and brings it up to date with
/// <see cref="Update"/>, so a row keeps its place and focus while its download goes further.
/// </summary>
public sealed partial class DownloadRowViewModel : ObservableObject
{
    private readonly DownloadManager _downloads;
    private readonly INavigationService _navigation;
    private readonly IConfirmation _confirmation;

    internal DownloadRowViewModel(DownloadItem item, DownloadManager downloads, INavigationService navigation, IConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(item);
        _downloads = downloads;
        _navigation = navigation;
        _confirmation = confirmation;
        DownloadId = item.DownloadId;
        MediaItemId = item.MediaItemId;
        Item = item;
        Update(item);
    }

    public string DownloadId { get; }

    /// <summary>The film's or the episode's own id: its title page.</summary>
    public string MediaItemId { get; }

    /// <summary>An episode's row, with a still rather than a poster.</summary>
    [ObservableProperty]
    public partial bool IsEpisode { get; private set; }

    /// <summary>A film's title; for an episode its series' title, "Example Series".</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>An episode's number and title, "E5 · Chapter Five", as the Apple app's season rows say it; empty for a film.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = "";

    /// <summary>The file's size, "8.0 GB"; empty while the server has not said it.</summary>
    [ObservableProperty]
    public partial string SizeText { get; private set; } = "";

    /// <summary>A film's poster or an episode's still on this PC; null until fetched.</summary>
    [ObservableProperty]
    public partial string? PosterPath { get; private set; }

    /// <summary>"Downloading · 42%", "Paused", "Downloaded" (<see cref="DownloadText.Status"/>).</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>How much of the file is here, 0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>The bar shows while the file comes, also while it is paused there.</summary>
    [ObservableProperty]
    public partial bool ShowsProgress { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    public partial bool CanPause { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool CanResume { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool CanRetry { get; private set; }

    /// <summary>What the row shows now.</summary>
    internal DownloadItem Item { get; private set; }

    /// <summary>Shows the download as it is now; <paramref name="item"/> is the same download, newer.</summary>
    public void Update(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.DownloadId != DownloadId)
        {
            throw new ArgumentException("A row shows one download.", nameof(item));
        }
        Item = item;
        IsEpisode = item.SeriesId is not null;
        Title = IsEpisode ? SeriesTitleOf(item) : item.Title;
        Detail = IsEpisode ? EpisodeLine(item) : "";
        SizeText = PrePlayText.FileSize(item.ExpectedBytes);
        PosterPath = item.PosterPath;
        StatusText = DownloadText.Status(item);
        Progress = item.Progress;
        // A download whose files are gone, or that the server no longer has, can only be deleted.
        bool usable = !item.FilesMissing && !item.IsNoLongerAvailable;
        bool running = item.State is DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading;
        ShowsProgress = usable && item.State == DownloadState.Downloading;
        CanPause = usable && running && !item.IsPausedByViewer;
        CanResume = usable && item.IsPausedByViewer;
        CanRetry = usable && item.State == DownloadState.Failed;
    }

    /// <summary>The series an episode belongs to, by the title it was downloaded with; its own title when that is missing.</summary>
    internal static string SeriesTitleOf(DownloadItem item) =>
        string.IsNullOrWhiteSpace(item.SeriesTitle) ? item.Title : item.SeriesTitle.Trim();

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseAsync() => _downloads.PauseAsync(DownloadId);

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => _downloads.ResumeAsync(DownloadId);

    /// <summary>A failed download goes on from the bytes it has.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => _downloads.ResumeAsync(DownloadId);

    /// <summary>Deletes the download from this PC after asking; the server hears it once the files are gone.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (await _confirmation.AskAsync(DownloadActions.DeleteTitle, DownloadActions.DeleteMessage, DownloadActions.DeleteAction))
        {
            await _downloads.DeleteAsync(DownloadId, CancellationToken.None);
        }
    }

    /// <summary>The film's or the episode's title page.</summary>
    [RelayCommand]
    private void Open() => _navigation.NavigateTo(AppRoute.Item, MediaItemId);

    private static string EpisodeLine(DownloadItem item) => item.EpisodeNumber is { } episode
        ? "E" + episode.ToString(CultureInfo.InvariantCulture) + " · " + item.Title
        : item.Title;
}
