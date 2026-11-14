using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>A season of a series' downloads ("Season 1", "Specials") and its episodes' cards, by number.</summary>
public sealed class DownloadSeason
{
    internal DownloadSeason(int number, string title, ICommand openCard)
    {
        Number = number;
        Title = title;
        OpenCard = openCard;
        Episodes = new ReadOnlyObservableCollection<LiveCard>(Shown);
    }

    /// <summary>The season's number; 0 for the specials, and for episodes without a season.</summary>
    public int Number { get; }

    public string Title { get; }

    public ReadOnlyObservableCollection<LiveCard> Episodes { get; }

    /// <summary>A card's click, the page's own (<see cref="DownloadedSeriesViewModel.OpenCardCommand"/>), for the season's grid.</summary>
    public ICommand OpenCard { get; }

    internal ObservableCollection<LiveCard> Shown { get; } = [];
}

/// <summary>
/// A series' downloads on this PC (<see cref="AppRoute.DownloadedSeries"/>), opened from its card on the Downloads page, as
/// the Apple app opens a series' downloads on a screen of their own: the series' name, how many episodes and the space they
/// take, then the episodes' cards by season, by number, with Specials last, as the Downloads page listed them. The cards and
/// their menus are the Downloads page's (<see cref="DownloadCards"/>, <see cref="DownloadCardActions"/>), and they follow the
/// downloads the same way while the page shows. Once the series has no downloads left on this PC, the page goes back.
/// </summary>
public sealed partial class DownloadedSeriesViewModel : ObservableObject, IDisposable
{
    public const string SpecialsTitle = "Specials";

    private readonly DownloadManager _downloads;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly DownloadsFollower _follow;
    private readonly ObservableCollection<DownloadSeason> _seasons = [];

    // On the UI thread: the series' downloads as last read, by download.
    private readonly Dictionary<string, DownloadItem> _items = new(StringComparer.Ordinal);

    // On the UI thread: which showing of which series this is (Open with another series, or Attach, starts the next), and the
    // showing in which the page asked to go back once its series had nothing left (-1 for none). The page is cached and shows
    // other series, so a departure asked for one showing must not move the viewer from the next.
    private int _showing;
    private int _leftIn = -1;

    public DownloadedSeriesViewModel(DownloadManager downloads, INavigationService navigation, IConfirmation confirmation, IUiDispatcher dispatcher,
        TimeProvider time)
    {
        _downloads = downloads;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _follow = new DownloadsFollower(downloads, dispatcher, time, Refresh);
        Actions = new DownloadCardActions(downloads, navigation, confirmation);
        Seasons = new ReadOnlyObservableCollection<DownloadSeason>(_seasons);
    }

    /// <summary>The series shown; empty before <see cref="Open"/>.</summary>
    public string SeriesId { get; private set; } = "";

    /// <summary>The series' name, as its episodes were downloaded with it.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>"3 episodes · 12.0 GB": how many, as the series' card counts them, and the space their files take.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public ReadOnlyObservableCollection<DownloadSeason> Seasons { get; }

    /// <summary>What the cards do: their click (<see cref="OpenCardCommand"/>) and their ⋮ menu's entries.</summary>
    public DownloadCardActions Actions { get; }

    /// <summary>
    /// Shows <paramref name="seriesId"/>'s downloads; another series than the one shown starts from nothing, so its cards are
    /// never the other's. On the UI thread, before <see cref="Attach"/>.
    /// </summary>
    public void Open(string seriesId)
    {
        ArgumentNullException.ThrowIfNull(seriesId);
        if (string.Equals(seriesId, SeriesId, StringComparison.Ordinal))
        {
            return;
        }
        SeriesId = seriesId;
        _showing++;
        Title = "";
        Summary = "";
        _items.Clear();
        _seasons.Clear();
    }

    /// <summary>The page shows, on the UI thread: it lists the series' downloads and follows them until <see cref="Detach"/>.</summary>
    public void Attach()
    {
        // Shown again before it unloaded, it still follows; another series opened meanwhile shows now all the same.
        if (_follow.Attach())
        {
            _showing++;
        }
        Show();
    }

    /// <summary>The page was left: it stops following the downloads, which hold it no longer.</summary>
    public void Detach() => _follow.Detach();

    public void Dispose() => Detach();

    /// <summary>A card's click: a whole download plays, any other opens its page.</summary>
    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null)
        {
            Actions.Invoke(card);
        }
    }

    private void Refresh(bool changed, IReadOnlyCollection<string> progressed)
    {
        if (changed)
        {
            Show();
            return;
        }
        bool any = false;
        foreach (string id in progressed)
        {
            if (_items.TryGetValue(id, out var item))
            {
                _items[id] = item with { BytesDone = _downloads.BytesDone(id) };
                any = true;
            }
        }
        if (any)
        {
            ShowCards();
        }
    }

    private void Show()
    {
        _items.Clear();
        foreach (var item in _downloads.Items.Where(i => string.Equals(i.SeriesId, SeriesId, StringComparison.Ordinal)))
        {
            _items[item.DownloadId] = item;
        }
        ShowCards();
        if (_items.Count == 0)
        {
            LeaveOnce();
        }
    }

    // Each season by number with Specials (0, or none) last, its episodes by number; seasons stay put while their cards change.
    private void ShowCards()
    {
        var episodes = _items.Values.ToList();
        if (episodes.Count > 0)
        {
            Title = DownloadCards.SeriesTitleOf(episodes.OrderBy(i => i.DownloadId, StringComparer.Ordinal).First());
            string size = DownloadCards.SizeText(episodes.Where(e => !e.FilesMissing).Sum(e => e.ExpectedBytes));
            string count = DownloadCards.EpisodeCount(episodes);
            Summary = size.Length > 0 ? $"{count} · {size}" : count;
        }
        var wanted = episodes.GroupBy(i => i.SeasonNumber is int number and > 0 ? number : 0)
            .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)
            .Select(g => (Number: g.Key, Cards: g.OrderBy(i => i.EpisodeNumber ?? int.MaxValue).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(i => DownloadCards.Download(i, _follow.RateOf(i.DownloadId))).ToList()))
            .ToList();
        for (int i = _seasons.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => w.Number == _seasons[i].Number))
            {
                _seasons.RemoveAt(i);
            }
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            var (number, cards) = wanted[i];
            int at = IndexOf(number);
            if (at < 0)
            {
                _seasons.Insert(i, new DownloadSeason(number, number == 0 ? SpecialsTitle : "Season " + number.ToString(CultureInfo.InvariantCulture),
                    OpenCardCommand));
                at = i;
            }
            else if (at != i)
            {
                _seasons.Move(at, i);
            }
            LiveCard.Sync(_seasons[i].Shown, cards);
        }
        _follow.Track(episodes);
    }

    private int IndexOf(int number)
    {
        for (int i = 0; i < _seasons.Count; i++)
        {
            if (_seasons[i].Number == number)
            {
                return i;
            }
        }
        return -1;
    }

    // Nothing of the series is left on this PC (its last episode was deleted, or it was gone when the page came back): the
    // Downloads page shows again, once. Posted, so it never goes back in the middle of the navigation that showed the page;
    // when its turn comes, only from the showing it was asked in, and only while the series still has nothing here. One
    // that came back meanwhile keeps the page, which asks again once it is empty again.
    private void LeaveOnce()
    {
        if (_leftIn == _showing)
        {
            return;
        }
        _leftIn = _showing;
        int showing = _showing;
        _dispatcher.Post(() =>
        {
            if (showing != _showing)
            {
                return;
            }
            if (_downloads.Items.Any(i => string.Equals(i.SeriesId, SeriesId, StringComparison.Ordinal)))
            {
                _leftIn = -1;
                return;
            }
            if (_follow.IsAttached && _navigation.Current == AppRoute.DownloadedSeries && _navigation.CanGoBack)
            {
                _navigation.GoBack();
            }
        });
    }
}
