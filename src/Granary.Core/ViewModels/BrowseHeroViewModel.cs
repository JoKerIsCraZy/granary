using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels;

/// <summary>
/// The title at the top of a page of rows (Home, For You): the card the pointer or the keyboard is on, in any row. The first
/// card pointed at shows at once; after that a card has to keep the pointer for <see cref="SettleTime"/>, so moving across a
/// row loads no backdrop for every card on the way. Until a card is pointed at, the first card of the first row; a reload
/// keeps the title while its rows still have it, and the pointer leaving the rows keeps the last one.
/// </summary>
public sealed partial class BrowseHeroViewModel : ObservableObject
{
    /// <summary>How long a card keeps the pointer before the top of the page changes to it.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(150);

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ITimer? _settle;
    private BrowseHeroTitle? _pointed;
    private bool _hasPointed;
    // Each point and reset; a settle timer that fires for an older one does nothing.
    private int _generation;

    internal BrowseHeroViewModel(IUiDispatcher dispatcher, TimeProvider time)
    {
        _dispatcher = dispatcher;
        _time = time;
    }

    /// <summary>The title shown; null while the page has no cards.</summary>
    [ObservableProperty]
    public partial BrowseHeroTitle? Shown { get; private set; }

    /// <summary>The pointer or the keyboard came to a card of <paramref name="item"/>, on <paramref name="server"/>.</summary>
    internal void Point(CatalogItemBase item, Uri server) => Point(BrowseHeroTitle.From(item, server));

    /// <summary>
    /// The page's rows changed to <paramref name="items"/>, their cards' titles in order: the title shown stays while they
    /// still have it, else the first one shows. Without a server, or a title, the hero hides.
    /// </summary>
    internal void Follow(IReadOnlyList<CatalogItemBase> items, Uri? server)
    {
        if (server is null)
        {
            Reset(null);
            return;
        }
        BrowseHeroTitle? Find(string key) => items.FirstOrDefault(i => i.ContentId == key) is { } found ? BrowseHeroTitle.From(found, server) : null;
        var title = (Shown is { } shown ? Find(shown.Key) : null) ?? (items.Count > 0 ? BrowseHeroTitle.From(items[0], server) : null);
        Reset(title, Find);
    }

    /// <summary>The pointer or the keyboard came to a card of <paramref name="title"/>.</summary>
    internal void Point(BrowseHeroTitle title)
    {
        if (!_hasPointed)
        {
            _hasPointed = true;
            Reset(title);
            return;
        }
        if (title.Key == Shown?.Key)
        {
            // Back on the shown title before the other one settled.
            CancelPending();
            return;
        }
        CancelPending();
        _pointed = title;
        int generation = _generation;
        _settle = _time.CreateTimer(_ => _dispatcher.Post(() => Settle(generation)), null, SettleTime, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The rows changed: shows <paramref name="title"/> (the shown title loaded again, or the first card) at once. A card the
    /// pointer rests on still settles when <paramref name="find"/> finds it in the new rows.
    /// </summary>
    internal void Reset(BrowseHeroTitle? title, Func<string, BrowseHeroTitle?>? find = null)
    {
        if (_pointed is { } pending && find?.Invoke(pending.Key) is { } fresh)
        {
            _pointed = fresh;
        }
        else
        {
            CancelPending();
        }
        Shown = title;
    }

    /// <summary>Another server or profile: its first card pointed at shows at once again.</summary>
    internal void Forget()
    {
        CancelPending();
        _hasPointed = false;
    }

    private void Settle(int generation)
    {
        if (generation == _generation && _pointed is { } title)
        {
            Reset(title);
        }
    }

    private void CancelPending()
    {
        _generation++;
        _settle?.Dispose();
        _settle = null;
        _pointed = null;
    }
}
