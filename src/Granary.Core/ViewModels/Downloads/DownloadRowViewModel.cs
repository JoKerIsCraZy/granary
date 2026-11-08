using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>What the one transfer button of a Downloads row does now.</summary>
public enum DownloadRowAction
{
    /// <summary>No button: the download is done, or can only be deleted.</summary>
    None,
    Pause,
    Resume,

    /// <summary>A failed download goes on from the bytes it has.</summary>
    Retry,
}

/// <summary>
/// A row of the Downloads page: a film, or an episode under its series and season, with its size and its status in the apps'
/// words, a bar while it downloads, one button that pauses, resumes or retries it, and Delete, which asks the title page's
/// question (<see cref="DownloadActions.DeleteTitle"/>). The page keeps one row per download and brings it up to date with
/// <see cref="Update"/>. Pause, Resume and Retry are one button whose action follows the state, so the button the viewer
/// pressed stays where it is, with the focus, when the download changes state.
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

    /// <summary>What the row's one transfer button does: Pause while it runs, Resume while paused, Retry once it failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrimaryAction), nameof(PrimaryActionLabel))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    public partial DownloadRowAction PrimaryAction { get; private set; }

    /// <summary>The button shows.</summary>
    public bool HasPrimaryAction => PrimaryAction != DownloadRowAction.None;

    /// <summary>"Pause", "Resume" or "Retry": the button's tooltip and automation name; empty without a button.</summary>
    public string PrimaryActionLabel => PrimaryAction switch
    {
        DownloadRowAction.Pause => "Pause",
        DownloadRowAction.Resume => "Resume",
        DownloadRowAction.Retry => "Retry",
        _ => "",
    };

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
        PrimaryAction = !usable ? DownloadRowAction.None
            : item.IsPausedByViewer ? DownloadRowAction.Resume
            : running ? DownloadRowAction.Pause
            : item.State == DownloadState.Failed ? DownloadRowAction.Retry
            : DownloadRowAction.None;
    }

    /// <summary>The series an episode belongs to, by the title it was downloaded with; its own title when that is missing.</summary>
    internal static string SeriesTitleOf(DownloadItem item) =>
        string.IsNullOrWhiteSpace(item.SeriesTitle) ? item.Title : item.SeriesTitle.Trim();

    /// <summary>
    /// Pauses, resumes or retries, as <see cref="PrimaryAction"/> says. It stays pressable while a press runs, so the button
    /// keeps the focus while its download changes state under it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasPrimaryAction), AllowConcurrentExecutions = true)]
    private Task PrimaryActionAsync() => PrimaryAction switch
    {
        DownloadRowAction.Pause => _downloads.PauseAsync(DownloadId),
        DownloadRowAction.Resume or DownloadRowAction.Retry => _downloads.ResumeAsync(DownloadId),
        _ => Task.CompletedTask,
    };

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
