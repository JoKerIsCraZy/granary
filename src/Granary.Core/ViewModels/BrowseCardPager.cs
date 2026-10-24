using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels;

/// <summary>
/// One cursor-paged catalog listing as cards (docs/api-contract.md, 7.4): page one, then the next pages
/// while the grid scrolls. Every page repeats the query, image parameters included, with the previous
/// page's cursor; a stale cursor (<c>invalid_cursor</c>) restarts the listing from page one.
/// </summary>
/// <remarks>
/// Used on the UI thread. The owner gives each listing its own instance and cancels it when it is
/// replaced, so an old listing's answers never reach the new one's cards.
/// </remarks>
/// <param name="toCard">Turns an item into its card; null leaves the item out.</param>
public sealed partial class BrowseCardPager(CatalogApi catalog, Uri server, CatalogQuery query, Func<CatalogItem, MediaCard?> toCard)
    : ObservableObject
{
    // A page whose items are all left out would stall a grid that loads more only when new cards appear.
    private const int MaxPagesPerLoad = 5;

    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private string? _cursor;

    public ObservableCollection<MediaCard> Cards { get; } = [];

    public CatalogQuery Query => query;

    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Page one has arrived.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>The listing's size as the server counts it; an estimate unless it says otherwise.</summary>
    public int Total { get; private set; }

    public async Task LoadFirstPageAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            Restart();
            await LoadAsync(cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Appends the next page; does nothing on the last page or while a page is on its way.</summary>
    public async Task LoadNextPageAsync(CancellationToken cancellationToken)
    {
        if (IsLoading || !IsLoaded || _cursor is null)
        {
            return;
        }
        IsLoading = true;
        try
        {
            await LoadAsync(cancellationToken);
        }
        catch (SiloApiException ex) when (ex.Code == ProblemCodes.InvalidCursor)
        {
            // An expired ranking session or changed data: the listing starts over.
            Restart();
            await LoadAsync(cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        for (int page = 0; page < MaxPagesPerLoad; page++)
        {
            var answer = await catalog.BrowseAsync(server, query.WithCursor(_cursor), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            int before = Cards.Count;
            foreach (var item in answer.Items)
            {
                if (_ids.Add(item.ContentId) && toCard(item) is { } card)
                {
                    Cards.Add(card);
                }
            }
            _cursor = answer.NextCursor;
            HasMore = _cursor is not null;
            Total = answer.Total;
            IsLoaded = true;
            if (Cards.Count > before || _cursor is null)
            {
                return;
            }
        }
    }

    private void Restart()
    {
        _cursor = null;
        _ids.Clear();
        Cards.Clear();
        HasMore = false;
        IsLoaded = false;
    }
}
