using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;

namespace Granary.Core.ViewModels;

/// <summary>
/// The title at the top of Home: the card the pointer or the keyboard is on, in any row. The first card pointed at shows at
/// once; after that a card has to keep the pointer for <see cref="SettleTime"/>, so moving across a row loads no backdrop
/// for every card on the way. Until a card is pointed at, the first card of the first row; a reload keeps the title while
/// its rows still have it, and the pointer leaving the rows keeps the last one.
/// </summary>
public sealed partial class HomeHeroViewModel : ObservableObject
{
    /// <summary>How long a card keeps the pointer before the top of Home changes to it.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(150);

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ITimer? _settle;
    private HomeHeroTitle? _pointed;
    private bool _hasPointed;
    // Each point and reset; a settle timer that fires for an older one does nothing.
    private int _generation;

    internal HomeHeroViewModel(IUiDispatcher dispatcher, TimeProvider time)
    {
        _dispatcher = dispatcher;
        _time = time;
    }

    /// <summary>The title shown; null while Home has no cards.</summary>
    [ObservableProperty]
    public partial HomeHeroTitle? Shown { get; private set; }

    /// <summary>The pointer or the keyboard came to a card of <paramref name="title"/>.</summary>
    internal void Point(HomeHeroTitle title)
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

    /// <summary>The rows changed: shows <paramref name="title"/> (the shown title loaded again, or the first card) at once.</summary>
    internal void Reset(HomeHeroTitle? title)
    {
        CancelPending();
        Shown = title;
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
