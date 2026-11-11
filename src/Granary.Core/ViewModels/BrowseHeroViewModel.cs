using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Settings;

namespace Granary.Core.ViewModels;

/// <summary>
/// The title at the top of a page of rows (Home, For You): the card the pointer or the keyboard is on, in any row. The first
/// card pointed at shows at once; after that a card has to keep the pointer for <see cref="SettleTime"/>, so moving across a
/// row loads no backdrop for every card on the way. Until a card is pointed at, the first card of the first row; a reload
/// keeps the title while its rows still have it, and the pointer leaving the rows keeps the last one. With the title preview
/// off (<see cref="IsOn"/>) it shows nothing.
/// </summary>
public sealed partial class BrowseHeroViewModel : ObservableObject
{
    /// <summary>How long a card keeps the pointer before the top of the page changes to it.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(150);

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly BrowseHeroSetting? _setting;
    private ITimer? _settle;
    private BrowseHeroTitle? _pointed;
    private bool _hasPointed;
    // Each point and reset; a settle timer that fires for an older one does nothing.
    private int _generation;
    // Whether the title preview was on when the hero last took its page's rows (Follow).
    private bool _tookRowsOn = true;

    internal BrowseHeroViewModel(IUiDispatcher dispatcher, TimeProvider time, BrowseHeroSetting? setting = null)
    {
        _dispatcher = dispatcher;
        _time = time;
        _setting = setting;
    }

    /// <summary>The title shown; null while the page has no cards, and while the title preview is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTitle))]
    public partial BrowseHeroTitle? Shown { get; private set; }

    /// <summary>
    /// A title shows, for the page's layout: x:Bind calls a function again only for an argument that is not null, so the rows'
    /// room under the hero follows this rather than <see cref="Shown"/>, which would leave it when the title goes.
    /// </summary>
    public bool HasTitle => Shown is not null;

    /// <summary>
    /// Whether the page has its hero: Settings → Navigation &amp; Cards → Title preview at the top (<see cref="BrowseHeroSetting"/>),
    /// on by default. Off, no title shows and a card pointed at starts no timer, so the page starts with its rows.
    /// </summary>
    public bool IsOn => _setting?.IsShown != false;

    /// <summary>
    /// The title preview was turned on or off since the hero last took its page's rows: the page gives them again as it shows,
    /// so a change in Settings applies there on the next visit.
    /// </summary>
    internal bool MissedSwitch => _tookRowsOn != IsOn;

    /// <summary>
    /// The pointer or the keyboard came to a card of <paramref name="item"/>, on <paramref name="server"/>; an unwatched
    /// episode shows what <paramref name="spoilers"/> leave of it, as its card does.
    /// </summary>
    internal void Point(CatalogItemBase item, Uri server, EpisodeSpoilers spoilers)
    {
        if (IsOn)
        {
            Point(BrowseHeroTitle.From(item, server, spoilers));
        }
    }

    /// <summary>
    /// The page's rows changed to <paramref name="items"/>, their cards' titles in order, or the spoiler switches changed: the
    /// title shown stays while they still have it, made again, else the first one shows. Without a server, or a title, the
    /// hero hides.
    /// </summary>
    internal void Follow(IReadOnlyList<CatalogItemBase> items, Uri? server, EpisodeSpoilers spoilers)
    {
        if (!IsOn)
        {
            TurnOff();
            return;
        }
        _tookRowsOn = true;
        if (server is null)
        {
            Reset(null);
            return;
        }
        BrowseHeroTitle? Find(string key) =>
            items.FirstOrDefault(i => i.ContentId == key) is { } found ? BrowseHeroTitle.From(found, server, spoilers) : null;
        var title = (Shown is { } shown ? Find(shown.Key) : null) ?? (items.Count > 0 ? BrowseHeroTitle.From(items[0], server, spoilers) : null);
        Reset(title, Find);
    }

    /// <summary>
    /// As <see cref="Follow(IReadOnlyList{CatalogItemBase}, Uri?, EpisodeSpoilers)"/>, for rows whose titles are made already
    /// (TMDB's).
    /// </summary>
    internal void Follow(IReadOnlyList<BrowseHeroTitle> titles)
    {
        if (!IsOn)
        {
            TurnOff();
            return;
        }
        _tookRowsOn = true;
        BrowseHeroTitle? Find(string key) => titles.FirstOrDefault(t => t.Key == key);
        Reset((Shown is { } shown ? Find(shown.Key) : null) ?? titles.FirstOrDefault(), Find);
    }

    /// <summary>The pointer or the keyboard came to a card of <paramref name="title"/>.</summary>
    internal void Point(BrowseHeroTitle title)
    {
        if (!IsOn)
        {
            return;
        }
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

    // The title preview is off: nothing shows and nothing waits to settle; once it is on again, the first card pointed at
    // shows at once.
    private void TurnOff()
    {
        _tookRowsOn = false;
        Forget();
        Reset(null);
    }

    private void Settle(int generation)
    {
        // Turned off while the card waited: the page takes its rows again as it shows (MissedSwitch), and that hides the title.
        if (!IsOn)
        {
            return;
        }
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
