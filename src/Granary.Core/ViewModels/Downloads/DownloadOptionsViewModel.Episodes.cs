using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>What a series' One time downloads, as the Apple app's series sheet offers it.</summary>
public enum OneTimeChoice
{
    /// <summary>The episode the series' page plays next.</summary>
    Episode,

    /// <summary>The season it is in.</summary>
    Season,

    /// <summary>Every season's episodes.</summary>
    AllEpisodes,
}

/// <summary>A row of a series' One time: its name, the line under it, and whether it has anything to download.</summary>
public sealed record OneTimeRow(OneTimeChoice Choice, string Title, string Detail, bool IsAvailable);

/// <summary>
/// Which episodes a season's or a series' One time starts. A series' has the Apple app's rows (<c>SeriesDownloadSheet.swift</c>):
/// This Episode, the one its page plays next ("S01E07 · Title", "This episode only", or "On this PC" and "Downloading" when it
/// is here already, then not to be chosen); Season N, the season it is in ("10 episodes"); and All Episodes ("Every season"),
/// which the dialog opens on, as the item that opens it says "Download series". "Only unwatched episodes" leaves out the ones
/// the viewer watched from a season's or a series' episodes, so a viewer half-way through the seventh gets the seventh to the
/// end: it shows only while one of the episodes left is watched, and then starts on. Monitor keeps its own rules.
/// </summary>
public sealed partial class DownloadOptionsViewModel
{
    public static string OnlyUnwatchedLabel => AppText.Get("granary.downloadOptions.onlyUnwatched");

    // The row the dialog downloads; a list's choice written back as nothing while its rows change leaves it.
    private OneTimeChoice _choice = OneTimeChoice.AllEpisodes;

    /// <summary>"Only unwatched episodes" is on: the episodes the viewer watched are not started.</summary>
    [ObservableProperty]
    public partial bool OnlyUnwatched { get; set; } = true;

    /// <summary>The switch shows: what is chosen is a season's or a series' episodes, and one of them is watched.</summary>
    [ObservableProperty]
    public partial bool ShowsOnlyUnwatched { get; private set; }

    /// <summary>A series' This Episode, Season N and All Episodes, as far as it has them; none for anything else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOneTimeRows))]
    public partial IReadOnlyList<OneTimeRow> OneTimeRows { get; private set; } = [];

    [ObservableProperty]
    public partial OneTimeRow? SelectedOneTimeRow { get; set; }

    /// <summary>The rows show: there is more than one to choose from.</summary>
    public bool ShowsOneTimeRows => OneTimeRows.Count > 1;

    // The switch counts and starts again what the offer chooses; its last error says nothing about the new choice.
    partial void OnOnlyUnwatchedChanged(bool value)
    {
        if (_offer is null)
        {
            return;
        }
        ErrorText = "";
        Choose();
        ShowSize();
    }

    partial void OnSelectedOneTimeRowChanged(OneTimeRow? value)
    {
        if (value is null || value.Choice == _choice || _offer is null)
        {
            return;
        }
        _choice = value.Choice;
        ErrorText = "";
        SelectedVersion = Choose();
        ShowSize();
    }

    // A series' rows as its offer was read, All Episodes chosen. A row's own words are its offer's; what the switch leaves
    // out shows in the size alone, so the rows stay as they are while it changes.
    private void ShowRows(DownloadOffer offer)
    {
        _choice = OneTimeChoice.AllEpisodes;
        SelectedOneTimeRow = null;
        OneTimeRows = offer is SeriesDownloadOffer series ? RowsOf(series) : [];
        SelectedOneTimeRow = OneTimeRows.FirstOrDefault(r => r.Choice == _choice);
    }

    private List<OneTimeRow> RowsOf(SeriesDownloadOffer series)
    {
        var rows = new List<OneTimeRow>();
        if (series.Episode is { Request: var episode })
        {
            var here = _downloads.ForItem(episode.MediaItemId);
            string detail = here is null ? AppText.Get("granary.downloadOptions.thisEpisodeOnly")
                : here.State == DownloadState.Completed ? AppText.Get("granary.downloadOptions.onThisPc")
                : AppText.Get("granary.downloads.status.downloading");
            rows.Add(new OneTimeRow(OneTimeChoice.Episode,
                DetailText.EpisodeTitle(episode.SeasonNumber ?? 0, episode.EpisodeNumber ?? 0, episode.Title), detail, here is null));
        }
        if (series.Season is { } season)
        {
            int left = season.Offer.Request.Episodes.Count;
            rows.Add(new OneTimeRow(OneTimeChoice.Season, SeasonName(season.SeasonNumber, season.Title),
                left > 0 ? AppText.Get("granary.downloadOptions.episodes", ("count", left)) : AppText.Get("granary.downloadOptions.onThisPc"), left > 0));
        }
        rows.Add(new OneTimeRow(OneTimeChoice.AllEpisodes, AppText.Get("granary.downloadOptions.allEpisodes"),
            AppText.Get("granary.downloadOptions.everySeason"), true));
        return rows;
    }

    // The offer as the choices take it: a series' row, then a season's or a series' episodes without the watched ones while
    // the switch is on.
    private DownloadOffer? Chosen()
    {
        var offer = _offer is SeriesDownloadOffer series ? _choice switch
        {
            OneTimeChoice.Episode when series.Episode is { } episode => episode,
            OneTimeChoice.Season when series.Season is { } season => season.Offer,
            _ => series,
        } : _offer;
        ShowsOnlyUnwatched = offer is SeasonDownloadOffer { HasWatched: true } or SeriesDownloadOffer { HasWatched: true };
        return offer switch
        {
            SeasonDownloadOffer { HasWatched: true } season when OnlyUnwatched => season.Unwatched(),
            SeriesDownloadOffer { HasWatched: true } whole when OnlyUnwatched => whole.Unwatched(),
            _ => offer,
        };
    }

    // The chosen row has something to download: This Episode has not once it is on this PC. A film's or an episode's own
    // dialog has no rows.
    private bool ChosenRowIsAvailable() => OneTimeRows.FirstOrDefault(r => r.Choice == _choice) is not { IsAvailable: false };

    // A season by the name the server gives it, else "Season 3", or "Specials" for season 0.
    private static string SeasonName(int number, string? title) => string.IsNullOrWhiteSpace(title) ? DetailText.SeasonLabel(number) : title.Trim();
}
