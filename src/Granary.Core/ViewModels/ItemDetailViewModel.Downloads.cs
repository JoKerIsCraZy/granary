using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Granary.Core.ViewModels.Downloads;

namespace Granary.Core.ViewModels;

// The Download items of the ⋯ menu, the web's overflow item (ActionBar.tsx): a film's or an episode's "Download" opens the
// download dialog with the version picked under the buttons, as the web offers it only for a title with files; a season's
// "Download season" downloads its episodes one by one. A title on this PC reads its status instead, opens the Downloads page,
// and has "Delete download" beside it, whatever the server says now. The episode cards of a season get the same entries.
public sealed partial class ItemDetailViewModel : ICardMenuSource
{
    private IDisposable? _followedDownloads;

    /// <summary>The ⋯ menu has a Download item: the server lets this profile download the title, or it is on this PC.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMoreMenu))]
    public partial bool ShowsDownload { get; private set; }

    /// <summary>"Download", "Download season", or the download's status: "Downloading · 42%", "Downloaded".</summary>
    [ObservableProperty]
    public partial string DownloadLabel { get; private set; } = DownloadActions.DownloadLabel;

    /// <summary>"Delete download" shows beside the item: the title is on this PC.</summary>
    [ObservableProperty]
    public partial bool CanDeleteDownload { get; private set; }

    /// <summary>The ⋯ button shows when its menu has something: the watchlist and collections, or a Download item.</summary>
    public bool ShowsMoreMenu => CanFavorite || ShowsDownload;

    /// <summary>The page shows: its Download items follow the downloads and what the server offers until <see cref="StopFollowingDownloads"/>.</summary>
    public void FollowDownloads()
    {
        if (_downloads is null || _followedDownloads is not null)
        {
            return;
        }
        _followedDownloads = _downloads.Follow(ShowDownloads);
        ShowDownloads();
    }

    /// <summary>The page was left: the app-wide downloads no longer hold it.</summary>
    public void StopFollowingDownloads()
    {
        _followedDownloads?.Dispose();
        _followedDownloads = null;
    }

    /// <summary>An episode card of the page's own list: Download, or its download's status and Delete download.</summary>
    public IReadOnlyList<CardMenuEntry> EntriesFor(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (_downloads is not { } downloads || card.Type != CatalogItemTypes.Episode || ListedEpisode(card.Id) is not { } episode
            || EpisodeRequest(episode) is null)
        {
            return [];
        }
        var state = downloads.StateOf(episode.ContentId);
        if (state.OpensDownloads)
        {
            return state.CanDelete
                ? [new(state.Label, OpenDownloadsPageCommand), new(DownloadActions.DeleteLabel, DeleteEpisodeDownloadCommand)]
                : [new(state.Label, OpenDownloadsPageCommand)];
        }
        return state.IsShown && UsableFiles(episode).Count > 0 ? [new(DownloadActions.DownloadLabel, DownloadEpisodeCommand)] : [];
    }

    /// <summary>The menu item: the download dialog, or the Downloads page for a title on this PC.</summary>
    [RelayCommand]
    private async Task OpenDownloadAsync()
    {
        if (_downloads is not { } downloads || _item is not { } item)
        {
            return;
        }
        if (item.Type == CatalogItemTypes.Season)
        {
            if (downloads.CanDownload && SeasonOffer(item) is { } season)
            {
                await downloads.OfferSeasonAsync(season.Request, season.Title, season.KnownBytes);
            }
        }
        else if (DownloadState() is { OpensDownloads: true })
        {
            downloads.OpenDownloads();
        }
        else if (DownloadState() is { IsShown: true } && TitleRequest(item) is { } request)
        {
            await downloads.OfferTitleAsync(request, TitleVersions(), RuntimeSeconds(item));
        }
        ShowDownloads();
    }

    [RelayCommand]
    private async Task DeleteDownloadAsync()
    {
        if (_downloads is { } downloads && CanDeleteDownload && _contentId.Length > 0)
        {
            await downloads.DeleteAsync(_contentId);
            ShowDownloads();
        }
    }

    /// <summary>An episode card's Download: the dialog with the episode's files, its first one chosen.</summary>
    [RelayCommand]
    private async Task DownloadEpisodeAsync(MediaCard? card)
    {
        if (_downloads is { } downloads && card is not null && ListedEpisode(card.Id) is { } episode && EpisodeRequest(episode) is { } request)
        {
            await downloads.OfferTitleAsync(request, EpisodeVersions(episode), Math.Max(0, episode.Runtime) * 60);
        }
    }

    [RelayCommand]
    private void OpenDownloadsPage() => _downloads?.OpenDownloads();

    [RelayCommand]
    private async Task DeleteEpisodeDownloadAsync(MediaCard? card)
    {
        if (_downloads is { } downloads && card is not null && ListedEpisode(card.Id) is { } episode)
        {
            await downloads.DeleteAsync(episode.ContentId);
        }
    }

    private void ShowDownloads()
    {
        var state = DownloadState();
        ShowsDownload = state.IsShown;
        DownloadLabel = state.Label.Length > 0 ? state.Label : DownloadActions.DownloadLabel;
        CanDeleteDownload = state.CanDelete;
    }

    private TitleDownloadState DownloadState()
    {
        if (_downloads is not { } downloads || _item is not { } item)
        {
            return TitleDownloadState.Hidden;
        }
        if (item.Type == CatalogItemTypes.Season)
        {
            return downloads.CanDownload && SeasonOffer(item) is not null
                ? new TitleDownloadState(true, DownloadActions.DownloadSeasonLabel, OpensDownloads: false, CanDelete: false)
                : TitleDownloadState.Hidden;
        }
        if (TitleRequest(item) is null)
        {
            return TitleDownloadState.Hidden;
        }
        var state = downloads.StateOf(_contentId);
        // A title without files has nothing to download, as on the web; one on this PC keeps its items.
        return state.OpensDownloads || _prePlayItem is not null ? state : TitleDownloadState.Hidden;
    }

    // A film, or an episode of its series, with the version under the buttons; the dialog sets the quality.
    private DownloadRequest? TitleRequest(CatalogItemDetail item)
    {
        string? file = Pickers.Version?.FileId;
        return item.Type switch
        {
            CatalogItemTypes.Movie => new DownloadRequest(_contentId, null, file, DownloadQualities.Original, item.Title, null, null, null),
            CatalogItemTypes.Episode when item.SeriesId is { Length: > 0 } series => new DownloadRequest(_contentId, series, file,
                DownloadQualities.Original, item.Title, item.SeriesTitle, item.SeasonNumber, item.EpisodeNumber),
            _ => null,
        };
    }

    // The Version picker's rows, with each file's size.
    private IReadOnlyList<DownloadVersionRow> TitleVersions()
    {
        var files = _prePlayItem?.Versions ?? [];
        return [.. Pickers.Versions.Select(v => new DownloadVersionRow(v.FileId, v.Summary,
            files.FirstOrDefault(f => f.FileId == v.FileId)?.FileSize ?? 0))];
    }

    // The runtime sizes a preset: the title's minutes, else the picked file's length.
    private int RuntimeSeconds(CatalogItemDetail item) =>
        item.Runtime is { } minutes && minutes > 0 ? minutes * 60 : (int)Math.Clamp(Pickers.Version?.Duration ?? 0, 0, int.MaxValue);

    // The season's episodes that have a file the server can read, each one download; the size is their first files'.
    private (SeasonDownloadRequest Request, string Title, long KnownBytes)? SeasonOffer(CatalogItemDetail item)
    {
        if (item.SeriesId is not { Length: > 0 } seriesId || item.SeasonNumber is not { } number)
        {
            return null;
        }
        var episodes = _episodes.Where(e => e.ContentId.Length > 0 && UsableFiles(e).Count > 0).ToList();
        if (episodes.Count == 0)
        {
            return null;
        }
        string seriesTitle = !string.IsNullOrWhiteSpace(item.SeriesTitle) ? item.SeriesTitle : item.Title;
        var request = new SeasonDownloadRequest(seriesId, seriesTitle, number,
            [.. episodes.Select(e => new SeasonEpisode(e.ContentId, e.EpisodeNumber, e.Title))], DownloadQualities.Original);
        return (request, item.Title, episodes.Sum(e => Math.Max(0, UsableFiles(e)[0].FileSize)));
    }

    // An episode card's download: its series is the season's (or the series page's own), its file the episode's first.
    private DownloadRequest? EpisodeRequest(Episode episode)
    {
        if (_item is not { } item)
        {
            return null;
        }
        var (seriesId, seriesTitle) = item.Type == CatalogItemTypes.Series ? (_contentId, item.Title) : (item.SeriesId, item.SeriesTitle);
        if (string.IsNullOrEmpty(seriesId) || episode.ContentId.Length == 0)
        {
            return null;
        }
        return new DownloadRequest(episode.ContentId, seriesId, UsableFiles(episode).FirstOrDefault()?.FileId, DownloadQualities.Original,
            episode.Title, seriesTitle, episode.SeasonNumber, episode.EpisodeNumber);
    }

    private Episode? ListedEpisode(string id) => _listsEpisodes && id.Length > 0 ? _episodes.FirstOrDefault(e => e.ContentId == id) : null;

    // An episode's files as the dialog lists them, the highest resolution first (web: sortByResolution).
    private static IReadOnlyList<DownloadVersionRow> EpisodeVersions(Episode episode) =>
        [.. PrePlayText.SortByResolution(UsableFiles(episode).Select(AsVersion))
            .Select(v => new DownloadVersionRow(v.FileId, PrePlayText.QualitySummary(v) is { Length: > 0 } summary ? summary : "Version " + v.FileId,
                v.FileSize))];

    // The files the server can read, as the player steps over the others (NextEpisode).
    private static IReadOnlyList<EpisodeFile> UsableFiles(Episode episode) => [.. episode.Files.Where(f => f.FileId.Length > 0 && !f.Unreadable)];

    private static FileVersion AsVersion(EpisodeFile file) => new()
    {
        FileId = file.FileId,
        Resolution = file.Resolution ?? "",
        CodecVideo = file.CodecVideo ?? "",
        Container = file.Container ?? "",
        Hdr = file.Hdr,
        FileSize = file.FileSize,
    };
}
