using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// What the Downloads page's cards do (<see cref="DownloadCards"/>): a click, and the entries of their ⋮ menu, which also opens
/// on right-click and with a remote's or a controller's Menu. A film's or an episode's download plays from this PC at a click
/// once it is whole, as a download's row plays in Silo's apps; until then the click opens its page, as the list's row did.
/// Its menu holds what the list's buttons did: Play, Pause, Resume or Retry, and Delete download, which asks first, and More
/// Info, its page, as the web's hero names it; and Play from Beginning once it was started or watched, as the web's card
/// menu offers it. A series' card opens its downloads
/// (<see cref="AppRoute.DownloadedSeries"/>); its menu has the Apple app's Delete all episodes, which asks first, and More
/// Info. A monitored series' card opens its download dialog on Monitor (<see cref="OpenMonitor"/>); its menu has the Apple
/// app's Go to series and Options. Each acts on the download as it is when it is picked.
/// </summary>
public sealed partial class DownloadCardActions : ICardMenuSource
{
    /// <summary>Play and More Info as the web's hero names them, Play from Beginning as its card menu does.</summary>
    public static string PlayLabel => AppText.Get("web.home.hero.play");
    public static string PlayFromBeginningLabel => AppText.Get("web.item.menu.playFromBeginning");
    public static string MoreInfoLabel => AppText.Get("web.home.hero.moreInfo");

    /// <summary>The Apple app's Pause and Resume of a download (<c>downloads.action.*</c>), and the web's Retry.</summary>
    public static string PauseLabel => AppText.Get("granary.downloads.pause");
    public static string ResumeLabel => AppText.Get("granary.downloads.resume");
    public static string RetryLabel => AppText.Get("common.retry");

    /// <summary>A monitored series' card's entries (the Apple app's <c>downloads.monitorList.goToSeries</c> and <c>.options</c>).</summary>
    public static string GoToSeriesLabel => AppText.Get("granary.downloads.goToSeries");
    public static string OptionsLabel => AppText.Get("granary.downloads.options");

    /// <summary>A series' card's entry (the Apple app's <c>downloads.manager.deleteAllEpisodes</c>).</summary>
    public static string DeleteSeriesLabel => AppText.Get("granary.downloads.deleteAllEpisodes");

    /// <summary>The Apple app's question before a series' downloads go, the Android app's words for what goes (adapted to this PC).</summary>
    public static string DeleteSeriesTitle => AppText.Get("granary.downloads.deleteFilesTitle");
    public static string DeleteSeriesMessage => AppText.Get("granary.downloads.deleteFilesMessage");

    private readonly DownloadManager _downloads;
    private readonly INavigationService _navigation;
    private readonly IConfirmation _confirmation;

    public DownloadCardActions(DownloadManager downloads, INavigationService navigation, IConfirmation confirmation)
    {
        _downloads = downloads;
        _navigation = navigation;
        _confirmation = confirmation;
    }

    /// <summary>What a monitored series' card opens: its download dialog on Monitor. None leaves those cards without a click.</summary>
    public Func<MediaCard, Task>? OpenMonitor { get; set; }

    /// <summary>A card's click: a whole download plays, any other opens its page; a series' card opens its downloads.</summary>
    public void Invoke(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Type == DownloadCards.MonitorType)
        {
            MonitorOptionsCommand.Execute(card);
        }
        else if (card.Type == DownloadCards.SeriesType)
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
        if (card.Type == DownloadCards.MonitorType)
        {
            return OpenMonitor is null ? [new(GoToSeriesLabel, MoreInfoCommand)] : [new(GoToSeriesLabel, MoreInfoCommand), new(OptionsLabel, MonitorOptionsCommand)];
        }
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
            // As the web's card menu offers it: a title with a resume point, or watched.
            if (download.IsWatched || download.ResumeMs is > 0)
            {
                entries.Add(new(PlayFromBeginningLabel, PlayFromBeginningCommand));
            }
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
    public static string? TransferLabel(DownloadItem download) => TransferOf(download) switch
    {
        Transfer.Pause => PauseLabel,
        Transfer.Resume => ResumeLabel,
        Transfer.Retry => RetryLabel,
        _ => null,
    };

    private static Transfer TransferOf(DownloadItem download)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (download.FilesMissing || download.IsNoLongerAvailable)
        {
            return Transfer.None;
        }
        return download.IsPausedByViewer ? Transfer.Resume
            : download.State is DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading ? Transfer.Pause
            : download.State == DownloadState.Failed ? Transfer.Retry
            : Transfer.None;
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

    /// <summary>Plays the download from this PC from its beginning, not where the profile left it.</summary>
    [RelayCommand]
    private void PlayFromBeginning(MediaCard? card)
    {
        if (card is not null && DownloadOf(card) is { IsPlayable: true } download)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.FromStart(download.MediaItemId));
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
        return TransferOf(download) switch
        {
            Transfer.Pause => _downloads.PauseAsync(download.DownloadId),
            Transfer.Resume or Transfer.Retry => _downloads.ResumeAsync(download.DownloadId),
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
        if (card.Type is DownloadCards.SeriesType or DownloadCards.MonitorType)
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
        else if (DownloadOf(card) is { } download)
        {
            _navigation.NavigateTo(AppRoute.Item, download.MediaItemId);
        }
    }

    /// <summary>A monitored series' download dialog, on Monitor, where its choices change and it stops.</summary>
    [RelayCommand]
    private Task MonitorOptionsAsync(MediaCard? card) =>
        card is { Type: DownloadCards.MonitorType } && OpenMonitor is { } open ? open(card) : Task.CompletedTask;

    /// <summary>
    /// Deletes the download from this PC after the title page's question; the server hears it once the files are gone. Each
    /// delete here acts in the profile its question was asked in (<see cref="DownloadManager.ActiveScope"/>).
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync(MediaCard? card)
    {
        if (card is null || _downloads.ActiveScope is not { } scope || DownloadOf(card) is null)
        {
            return;
        }
        if (await _confirmation.AskAsync(DownloadActions.DeleteTitle, DownloadActions.DeleteMessage, DownloadActions.DeleteAction))
        {
            await _downloads.DeleteAsync(scope, card.Id, CancellationToken.None);
        }
    }

    /// <summary>
    /// Deletes every download of the series after asking, in the Apple app's words ("Delete 3 Downloads"): the ones the question
    /// counted, as a single delete goes, and its episodes waiting for the server's limit of downloads with them.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSeriesAsync(MediaCard? card)
    {
        if (card is null || _downloads.ActiveScope is not { } scope || EpisodesOf(card.Id) is not { Count: > 0 } episodes)
        {
            return;
        }
        string action = AppText.Get("granary.downloads.deleteFilesConfirm", ("count", episodes.Count), ("count_text", AppText.Number(episodes.Count)));
        if (!await _confirmation.AskAsync(DeleteSeriesTitle, DeleteSeriesMessage, action))
        {
            return;
        }
        // The line first: a delete that reaches the server frees a place, and would start one of them.
        await _downloads.DropWaitingAsync(scope, card.Id, CancellationToken.None);
        await _downloads.DeleteAllAsync(scope, [.. episodes.Select(e => e.DownloadId)], CancellationToken.None);
    }

    // What the transfer entry does, whatever its words.
    private enum Transfer
    {
        None,
        Pause,
        Resume,
        Retry,
    }

    private DownloadItem? DownloadOf(MediaCard card) => card.Type == DownloadCards.DownloadType ? _downloads.Find(card.Id) : null;

    private List<DownloadItem> EpisodesOf(string seriesId) =>
        [.. _downloads.Items.Where(i => string.Equals(i.SeriesId, seriesId, StringComparison.Ordinal))];
}
