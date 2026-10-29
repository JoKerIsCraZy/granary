using System.Collections;
using System.Collections.Specialized;
using Granary.Core.Artwork;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.App.Controls;

/// <summary>
/// Prefetches the artwork of a card list, and of cards added to it later, so a row or grid shows images the
/// moment they scroll into view, and works out their thumbhash blurs ahead. A new list, a reset list or
/// <see cref="Follow"/>(null) drops what is still queued.
/// </summary>
/// <remarks>The owner follows its list only while loaded: the list's event would otherwise keep it alive.</remarks>
internal sealed class CardArtworkPrefetch
{
    private IEnumerable? _cards;
    private CancellationTokenSource _batch = new();

    private static IArtworkPrefetcher Prefetcher => field ??= App.Services.GetRequiredService<IArtworkPrefetcher>();

    /// <summary>Follows <paramref name="cards"/> (any list of <see cref="MediaCard"/>) instead of the previous list; null stops.</summary>
    public void Follow(object? cards)
    {
        if (ReferenceEquals(cards, _cards))
        {
            return;
        }
        if (_cards is INotifyCollectionChanged previous)
        {
            previous.CollectionChanged -= OnCollectionChanged;
        }
        Restart();
        _cards = cards as IEnumerable;
        if (_cards is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += OnCollectionChanged;
        }
        Prefetch(_cards);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Restart();
            Prefetch(_cards);
        }
        else
        {
            Prefetch(e.NewItems);
        }
    }

    private void Restart()
    {
        _batch.Cancel();
        _batch = new CancellationTokenSource();
    }

    private void Prefetch(IEnumerable? items)
    {
        if (items is null)
        {
            return;
        }
        List<MediaCard> cards = [.. items.OfType<MediaCard>()];
        List<Uri> urls = [.. cards.Select(c => c.ImageUrl).OfType<Uri>()];
        if (urls.Count > 0)
        {
            Prefetcher.Prefetch(urls, _batch.Token);
        }
        // Their blurs too, off the UI thread: a card scrolled in then only makes its bitmap.
        Thumbhashes.Warm(cards.Select(c => c.ImageThumbhash), _batch.Token);
    }
}
