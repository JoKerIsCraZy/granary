using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// A movie's box set (docs/api-contract.md, 7.18), as the web's MovieBoxSetRow shows it before More Like This: the set's films
// in release order, this one marked "This film", the set's name as the row's title and Explore all to its page. It is asked
// for only while one of the profile's libraries has box sets on (BoxSetLibraries), as the web asks. Nothing shows while it
// loads, for a movie in no box set, or on a server without box sets (404): a failed read only leaves the row out, as the
// apps keep their rows quiet.
public sealed partial class ItemDetailViewModel
{
    private CatalogBoxSetDetail? _boxSet;

    /// <summary>The box set's name, the row's title ("Masterplan Box Set"); empty without one.</summary>
    [ObservableProperty]
    public partial string BoxSetTitle { get; private set; } = "";

    /// <summary>The box set's films in release order, this movie's card marked <see cref="CurrentMark.ThisFilm"/>; empty without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBoxSet))]
    public partial IReadOnlyList<MediaCard> BoxSetFilms { get; private set; } = [];

    public bool HasBoxSet => BoxSetFilms.Count > 0;

    /// <summary>The row's Explore all: the box set's own page, in the library its films are from.</summary>
    [RelayCommand]
    private void OpenBoxSet()
    {
        if (_boxSet is { } boxSet && HasBoxSet)
        {
            _navigation.NavigateTo(AppRoute.BoxSet, new BoxSetRouteParameter(boxSet.Id, boxSet.LibraryId, boxSet.Title));
        }
    }

    // The server and profile are the ones the page was loaded for, not whichever acts by the time the item came.
    private async Task LoadBoxSetAsync(Uri server, string? profileId, ImageParameters images, int generation, CancellationToken cancellationToken)
    {
        if (_boxSets is null || _boxSetLibraries is null)
        {
            return;
        }
        // Only while a library has box sets on, as the web's useItemBoxSet: otherwise there is nothing to ask for. It answers
        // no on any failure of its own.
        if (!await _boxSetLibraries.AnyAsync(server, profileId, cancellationToken))
        {
            return;
        }
        CatalogBoxSetDetail? boxSet;
        try
        {
            boxSet = await _boxSets.GetForItemAsync(server, _contentId, images: images, cancellationToken: cancellationToken);
        }
        // ArgumentException: the server named an id the client refuses to put in a path ("", "..").
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is ArgumentException)
        {
            // A server without box sets answers 404, which is nothing to report.
            if (!cancellationToken.IsCancellationRequested && ex is not SiloApiException { Status: 404 })
            {
                _logger.LogWarning("Loading the box set of item {ItemId} failed: {Failure}", ItemId, ApiFailureText.Describe(ex));
            }
            return;
        }
        if (generation != _generation || boxSet is not { Items.Count: > 0 })
        {
            return;
        }
        _boxSet = boxSet;
        BoxSetTitle = boxSet.Title;
        BoxSetFilms = [.. boxSet.Items.Where(film => film.ContentId.Length > 0).Select(film => FilmCard(film, server))];
    }

    // A film opens its own page; this one's card opens nothing (Open), as the apps' rows.
    private MediaCard FilmCard(CatalogItem film, Uri server)
    {
        var card = MediaCardFactory.Poster(film, server);
        return film.ContentId == _contentId ? card with { CurrentMark = CurrentMark.ThisFilm } : card;
    }

    private void ResetBoxSet()
    {
        _boxSet = null;
        BoxSetTitle = "";
        BoxSetFilms = [];
    }
}
