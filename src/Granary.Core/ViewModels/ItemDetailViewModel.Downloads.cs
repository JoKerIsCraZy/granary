using System.Collections.Frozen;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Downloads;

namespace Granary.Core.ViewModels;

// The Download items of the ⋯ menu, the web's overflow item (ActionBar.tsx): a film's or an episode's "Download" opens the
// download dialog with the version picked under the buttons, as the web offers it only for a title with files; a season's
// "Download season" downloads its episodes one by one. A title on this PC reads its status instead, opens the Downloads page,
// and has "Delete download" beside it, whatever the server says now. Its cards carry their own (CardDownloads), so the page
// adds none to them.
public sealed partial class ItemDetailViewModel
{
    private IDisposable? _followedDownloads;

    // The films and episodes whose downloads the page shows, set on the UI thread and read on the transfers' threads.
    private volatile FrozenSet<string> _downloadItems = FrozenSet<string>.Empty;

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

    /// <summary>
    /// The page's own items of the ⋯ menu are there: the watchlist and collections, or a Download item. The hero shows ⋯ also
    /// for Start a Watch Party alone (<c>StartPartyViewModel.IsOffered</c>), which depends on the server's rooms and this
    /// profile's room, which only the party's view model follows.
    /// </summary>
    public bool ShowsMoreMenu => CanFavorite || ShowsDownload;

    /// <summary>The page shows: its Download items follow the downloads and what the server offers until <see cref="StopFollowingDownloads"/>.</summary>
    public void FollowDownloads()
    {
        if (_downloads is null || _followedDownloads is not null)
        {
            return;
        }
        _followedDownloads = _downloads.Follow(id => _downloadItems.Contains(id), ShowDownloads);
        ShowDownloads();
    }

    /// <summary>The page was left: the app-wide downloads no longer hold it.</summary>
    public void StopFollowingDownloads()
    {
        _followedDownloads?.Dispose();
        _followedDownloads = null;
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
            if (downloads.CanDownload && SeasonOffer(downloads, item) is { } season)
            {
                await downloads.OfferSeasonAsync(season.Request, season.SeasonTitle, season.KnownBytes);
            }
        }
        else if (DownloadState() is { OpensDownloads: true })
        {
            downloads.OpenDownloads();
        }
        else if (DownloadState() is { IsShown: true } && TitleRequest(item) is { } request)
        {
            await downloads.OfferTitleAsync(request, DownloadOffers.TitleVersions(_prePlayItem?.Versions ?? [], Pickers),
                DownloadOffers.RuntimeSeconds(item, Pickers.Version));
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

    private void ShowDownloads()
    {
        _downloadItems = DownloadItems();
        var state = DownloadState();
        ShowsDownload = state.IsShown;
        DownloadLabel = state.Label.Length > 0 ? state.Label : DownloadActions.DownloadLabel;
        CanDeleteDownload = state.CanDelete;
    }

    // The title, and the episodes it lists, whose downloads change its items: a season's Download season counts the ones
    // not on this PC.
    private FrozenSet<string> DownloadItems()
    {
        if (_item is null)
        {
            return FrozenSet<string>.Empty;
        }
        IEnumerable<string> episodes = _listsEpisodes ? _episodes.Select(e => e.ContentId) : [];
        return episodes.Append(_contentId).Where(id => id.Length > 0).ToFrozenSet(StringComparer.Ordinal);
    }

    private TitleDownloadState DownloadState()
    {
        if (_downloads is not { } downloads || _item is not { } item)
        {
            return TitleDownloadState.Hidden;
        }
        if (item.Type == CatalogItemTypes.Season)
        {
            return downloads.CanDownload && SeasonOffer(downloads, item) is not null
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
    private DownloadRequest? TitleRequest(CatalogItemDetail item) => DownloadOffers.TitleRequest(item, _contentId, Pickers.Version?.FileId);

    // The season's episodes with a file that are not on this PC yet; none left: no Download season.
    private SeasonDownloadOffer? SeasonOffer(DownloadActions downloads, CatalogItemDetail item) =>
        DownloadOffers.ForSeason(item, _episodes, id => downloads.StateOf(id).OpensDownloads);
}
