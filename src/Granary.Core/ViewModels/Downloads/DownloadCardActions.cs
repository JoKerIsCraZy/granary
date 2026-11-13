using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// What the Downloads page's cards do (<see cref="DownloadCards"/>): a click, and the entries of their ⋮ menu, which also opens
/// on right-click and with a remote's or a controller's Menu. A film's or an episode's download plays from this PC at a click
/// once it is whole, as a download's row plays in Silo's apps; until then the click opens its page, as the list's row did.
/// Its menu holds what the list's buttons did: Play, Pause, Resume or Retry, and Delete download, which asks first, and More
/// Info, its page, as the web's hero names it; Play from Beginning once it was started. A series' card opens its downloads
/// (<see cref="AppRoute.DownloadedSeries"/>); its menu has the Apple app's Delete All Episodes, which asks first, and More
/// Info. Each acts on the download as it is when it is picked.
/// </summary>
public sealed partial class DownloadCardActions : ICardMenuSource
{
    public const string PlayLabel = "Play";
    public const string PauseLabel = "Pause";
    public const string ResumeLabel = "Resume";
    public const string RetryLabel = "Retry";
    public const string MoreInfoLabel = "More Info";

    /// <summary>A series' card's entry (the Apple app's <c>Delete All Episodes</c>).</summary>
    public const string DeleteSeriesLabel = "Delete All Episodes";

    /// <summary>The Apple app's question before a series' downloads go, the Android app's words for what goes (adapted to this PC).</summary>
    public const string DeleteSeriesTitle = "Delete downloaded files?";
    public const string DeleteSeriesMessage = "This removes the downloaded copies from this PC. Nothing is deleted from your Silo server.";

    private readonly DownloadManager _downloads;
    private readonly INavigationService _navigation;
    private readonly IConfirmation _confirmation;

    public DownloadCardActions(DownloadManager downloads, INavigationService navigation, IConfirmation confirmation)
    {
        _downloads = downloads;
        _navigation = navigation;
        _confirmation = confirmation;
    }

    /// <summary>A card's click: a whole download plays, any other opens its page; a series' card opens its downloads.</summary>
    public void Invoke(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Type == DownloadCards.SeriesType)
        {
            _navigation.NavigateTo(AppRoute.DownloadedSeries, card.Id);
        }
        else if (DownloadOf(card) is { } download)
        {
            if (download.IsPlayable)
            {
                _navigation.NavigateTo(AppRoute.Player, PlayRequest.Resume(download.MediaItemId));
            }
            else
            {
                _navigation.NavigateTo(AppRoute.Item, download.MediaItemId);
            }
        }
    }

    public IReadOnlyList<CardMenuEntry> EntriesFor(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Type == DownloadCards.SeriesType)
        {
            return EpisodesOf(card.Id).Count == 0 ? [] : [new(MoreInfoLabel, MoreInfoCommand), new(DeleteSeriesLabel, DeleteSeriesCommand)];
        }
        if (DownloadOf(card) is not { } download)
        {
            return [];
        }
        var entries = new List<CardMenuEntry>();
        if (download.IsPlayable)
        {
            entries.Add(new(PlayLabel, PlayCommand));
        }
        if (TransferLabel(download) is { } transfer)
        {
            entries.Add(new(transfer, TransferCommand));
        }
        entries.Add(new(MoreInfoLabel, MoreInfoCommand));
        entries.Add(new(DownloadActions.DeleteLabel, DeleteCommand));
        return entries;
    }

    /// <summary>
    /// What the transfer entry does now: Pause while it runs or waits its turn, Resume once the viewer paused it, Retry once it
    /// failed; none when it is whole, or can only be deleted (its files are gone, or the server no longer has it).
    /// </summary>
    public static string? TransferLabel(DownloadItem download)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (download.FilesMissing || download.IsNoLongerAvailable)
        {
            return null;
        }
        return download.IsPausedByViewer ? ResumeLabel
            : download.State is DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading ? PauseLabel
            : download.State == DownloadState.Failed ? RetryLabel
            : null;
    }

    /// <summary>Plays the download from this PC, from where the profile left it; the player finds the download itself.</summary>
    [RelayCommand]
    private void Play(MediaCard? card)
    {
        if (card is not null && DownloadOf(card) is { IsPlayable: true } download)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.Resume(download.MediaItemId));
        }
    }

    /// <summary>Pauses, resumes or retries, as <see cref="TransferLabel"/> says for the download as it is now.</summary>
    [RelayCommand]
    private Task TransferAsync(MediaCard? card)
    {
        if (card is null || DownloadOf(card) is not { } download)
        {
            return Task.CompletedTask;
        }
        return TransferLabel(download) switch
        {
            PauseLabel => _downloads.PauseAsync(download.DownloadId),
            ResumeLabel or RetryLabel => _downloads.ResumeAsync(download.DownloadId),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>The film's or the episode's page; a series' card the series'.</summary>
    [RelayCommand]
    private void MoreInfo(MediaCard? card)
    {
        if (card is null)
        {
            return;
        }
        if (card.Type == DownloadCards.SeriesType)
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
        else if (DownloadOf(card) is { } download)
        {
            _navigation.NavigateTo(AppRoute.Item, download.MediaItemId);
        }
    }

    /// <summary>Deletes the download from this PC after the title page's question; the server hears it once the files are gone.</summary>
    [RelayCommand]
    private async Task DeleteAsync(MediaCard? card)
    {
        if (card is null || DownloadOf(card) is null)
        {
            return;
        }
        if (await _confirmation.AskAsync(DownloadActions.DeleteTitle, DownloadActions.DeleteMessage, DownloadActions.DeleteAction))
        {
            await _downloads.DeleteAsync(card.Id, CancellationToken.None);
        }
    }

    /// <summary>
    /// Deletes every download of the series after asking, in the Apple app's words ("Delete 3 Downloads"): the ones the question
    /// counted, as a single delete goes.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSeriesAsync(MediaCard? card)
    {
        if (card is null || EpisodesOf(card.Id) is not { Count: > 0 } episodes)
        {
            return;
        }
        string action = episodes.Count == 1
            ? "Delete Download"
            : $"Delete {episodes.Count.ToString(CultureInfo.InvariantCulture)} Downloads";
        if (!await _confirmation.AskAsync(DeleteSeriesTitle, DeleteSeriesMessage, action))
        {
            return;
        }
        foreach (var episode in episodes)
        {
            await _downloads.DeleteAsync(episode.DownloadId, CancellationToken.None);
        }
    }

    private DownloadItem? DownloadOf(MediaCard card) => card.Type == DownloadCards.DownloadType ? _downloads.Find(card.Id) : null;

    private List<DownloadItem> EpisodesOf(string seriesId) =>
        [.. _downloads.Items.Where(i => string.Equals(i.SeriesId, seriesId, StringComparison.Ordinal))];
}
