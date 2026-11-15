using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// The Download entries of a film's, an episode's, a season's and a series' card in its ⋯ menu, wherever the card shows (Home's
/// rows, the libraries, search, collections, a title page's rows and seasons), as the title page's ⋯ has them
/// (<see cref="DownloadActions"/>): "Download", "Download season" or "Download series" while the server lets the profile
/// download; a title on this PC its status, which opens the Downloads page, and "Delete download", whatever the server says
/// now. The entries come from what this PC knows, so the menu reads nothing from the server; the dialog reads the title's
/// files once it opens (<see cref="DownloadOfferReader"/>). One for the app: the commands take the card.
/// </summary>
public sealed partial class CardDownloads(DownloadActions actions, DownloadOfferReader offers, ILogger<CardDownloads> logger)
{
    /// <summary>The card's Download entries, in the menu's order; none for a card that has nothing to download.</summary>
    public IReadOnlyList<CardMenuEntry> EntriesFor(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        // A movie's own card in its box set's row has no menu of its own: the page's buttons hold it.
        if (card.IsPlaceholder || card.Id.Length == 0 || card.CurrentMark == CurrentMark.ThisFilm)
        {
            return [];
        }
        if (card.Type is CatalogItemTypes.Season or CatalogItemTypes.Series)
        {
            string label = card.Type == CatalogItemTypes.Season ? DownloadActions.DownloadSeasonLabel : DownloadActions.DownloadSeriesLabel;
            return actions.CanDownload ? [new(label, DownloadCommand)] : [];
        }
        if (card.Type is not (CatalogItemTypes.Movie or CatalogItemTypes.Episode))
        {
            return [];
        }
        var state = actions.StateOf(card.Id);
        if (state.OpensDownloads)
        {
            return state.CanDelete
                ? [new(state.Label, OpenDownloadsCommand), new(DownloadActions.DeleteLabel, DeleteCommand)]
                : [new(state.Label, OpenDownloadsCommand)];
        }
        return state.IsShown ? [new(DownloadActions.DownloadLabel, DownloadCommand)] : [];
    }

    // The download dialog for the card's title, which reads its files while it shows. A menu's command: what fails is logged
    // by its kind, never thrown at the menu, and the menu's Download can be chosen again. A card has no place of its own to
    // say it, as the title page has.
    [RelayCommand]
    private async Task DownloadAsync(MediaCard? card)
    {
        if (card is null)
        {
            return;
        }
        try
        {
            // A series' card offers Monitor beside it, as the Apple app's series sheet.
            var monitor = card.Type == CatalogItemTypes.Series ? new SeriesMonitorTarget(card.Id, card.Title) : null;
            await actions.OfferReadAsync(cancellationToken => offers.ReadAsync(card.Id, card.Type, cancellationToken), monitor);
        }
        catch (Exception ex)
        {
            logger.LogError("Opening the download dialog from a card's menu failed ({Failure})", DownloadLog.Failure(ex));
        }
    }

    [RelayCommand]
    private void OpenDownloads() => actions.OpenDownloads();

    // A menu's command: what fails is logged by its kind (a file error's message names a folder), never thrown at the menu.
    [RelayCommand]
    private async Task DeleteAsync(MediaCard? card)
    {
        if (card is null)
        {
            return;
        }
        try
        {
            await actions.DeleteAsync(card.Id);
        }
        catch (Exception ex)
        {
            logger.LogError("Deleting a download from a card's menu failed ({Failure})", DownloadLog.Failure(ex));
        }
    }
}
