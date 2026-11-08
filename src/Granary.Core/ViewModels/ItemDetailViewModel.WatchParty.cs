using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Detail;
using Granary.Core.ViewModels.WatchParty;

namespace Granary.Core.ViewModels;

// "Start a Watch Party" from the page, as the web's detail pages hand their item to DetailWatchTogether.tsx: a movie or an
// episode with a file is put up itself; a series its next episode (SeriesContent.tsx), a season its first unwatched one
// with a file (SeasonContent.tsx), and both offer the series' other episodes to pick from instead.
public sealed partial class ItemDetailViewModel
{
    /// <summary>
    /// The page has something to start a party with: a movie with a file, a series, an episode, or a season once its
    /// episodes are listed (the page shows before them, and the sheet would open with nothing up).
    /// </summary>
    public bool OffersWatchParty => State == ItemDetailState.Ready && _item is { } item && item.Type switch
    {
        CatalogItemTypes.Movie => item.Versions.Count > 0,
        CatalogItemTypes.Series => _contentId.Length > 0,
        CatalogItemTypes.Season => !string.IsNullOrWhiteSpace(item.SeriesId) && _listsEpisodes,
        CatalogItemTypes.Episode => item.Versions.Count > 0 || !string.IsNullOrWhiteSpace(item.SeriesId),
        _ => false,
    };

    /// <summary>The page as the start sheet shows it, and what the party would put up first; null when it offers none.</summary>
    public StartPartyRequest? WatchPartyRequest()
    {
        if (!OffersWatchParty || _item is not { } item || _serverBase is not { } server)
        {
            return null;
        }
        string? seriesId = PartyText(item.Type == CatalogItemTypes.Series ? _contentId : item.SeriesId);
        string? seriesTitle = PartyText(item.Type == CatalogItemTypes.Series ? item.Title : item.SeriesTitle);
        // An unwatched episode's own still stays hidden here too, as everywhere the profile hides it.
        bool hidesPoster = item.Type == CatalogItemTypes.Episode
            && Spoilers.HidesImage(!IsWatched && EpisodeSpoilers.IsUnwatched(_watch), item.PosterIsEpisodeStill);
        return new StartPartyRequest
        {
            Title = item.Type == CatalogItemTypes.Season ? DisplayTitle : item.Title,
            Caption = PartyCaption(item),
            Poster = hidesPoster ? null : ArtworkUrl.Resolve(server, item.PosterUrl),
            PosterThumbhash = item.PosterThumbhash ?? "",
            PosterPath = PartyText(item.PosterUrl),
            Target = PartyTarget(item, seriesTitle),
            NextUpId = item.Type == CatalogItemTypes.Series ? PartyText(item.PlayContentId) : null,
            SeriesId = seriesId,
            SeriesTitle = seriesTitle,
            SeasonNumber = item.Type == CatalogItemTypes.Series ? item.PlaySeasonNumber : item.SeasonNumber,
        };
    }

    // What the offer reads changes with these: the item read (Loading, Ready), and a season's episodes listed or found
    // empty (the cards, or the empty note, follow every new list).
    partial void OnStateChanged(ItemDetailState value) => OnPropertyChanged(nameof(OffersWatchParty));

    partial void OnEpisodesChanged(IReadOnlyList<DetailEpisode> value) => OnPropertyChanged(nameof(OffersWatchParty));

    partial void OnShowsNoEpisodesChanged(bool value) => OnPropertyChanged(nameof(OffersWatchParty));

    // The web's line under the sheet's title: a series' "Series · 3 seasons", a season's name, else the year.
    private static string PartyCaption(CatalogItemDetail item) => item.Type switch
    {
        CatalogItemTypes.Series => item.SeasonCount is > 0 and var count
            ? string.Create(CultureInfo.InvariantCulture, $"Series · {count} seasons")
            : "Series",
        CatalogItemTypes.Season => item.Title,
        _ => item.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
    };

    private StartPartyTarget? PartyTarget(CatalogItemDetail item, string? seriesTitle)
    {
        switch (item.Type)
        {
            case CatalogItemTypes.Movie when item.Versions.Count > 0:
                return new StartPartyTarget(_contentId, CatalogItemTypes.Movie, item.Title, item.Year?.ToString(CultureInfo.InvariantCulture) ?? "");
            case CatalogItemTypes.Episode when item.Versions.Count > 0:
                string code = item.SeasonNumber is { } season && item.EpisodeNumber is { } number ? StartPartyTarget.EpisodeCode(season, number) : "";
                return new StartPartyTarget(_contentId, CatalogItemTypes.Episode, item.Title, code, seriesTitle);
            case CatalogItemTypes.Season:
                // Only episodes with a file can play; the first the profile has not watched, else the first of them.
                var playable = _episodes.Where(e => e.Files.Count > 0 && e.ContentId.Length > 0).ToList();
                var first = playable.FirstOrDefault(e => e.UserData?.Played != true) ?? playable.FirstOrDefault();
                return first is null
                    ? null
                    : new StartPartyTarget(first.ContentId, CatalogItemTypes.Episode, first.Title,
                        StartPartyTarget.EpisodeCode(first.SeasonNumber, first.EpisodeNumber), seriesTitle);
            default:
                // A series names its next episode once the sheet read it.
                return null;
        }
    }

    private static string? PartyText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
