using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;

namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// A TMDB card's Request that failed, said on the page that shows the card, as the web's toast: "Failed to submit request",
/// gone after <see cref="ShowTime"/>, when the page reads its titles again, or with another profile. A request that went
/// through clears it only when it is the same card's, so another card's success does not hide a failure early. The page
/// attaches it while it shows; every page with TMDB cards has its own (<see cref="MediaCardActions.RequestAnswered"/>).
/// </summary>
public sealed partial class CardRequestNotice : ObservableObject
{
    /// <summary>How long a failure shows: the web's toast, sonner's default duration.</summary>
    public static readonly TimeSpan ShowTime = TimeSpan.FromSeconds(4);

    private readonly MediaCardActions? _actions;
    private readonly ISessionManager? _session;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher? _dispatcher;
    private ITimer? _timer;
    private string? _failedCard;
    // Counts the failures shown: the timer of an earlier one clears nothing of a later one.
    private int _shown;
    private bool _attached;

    public CardRequestNotice(MediaCardActions actions, ISessionManager session, TimeProvider time, IUiDispatcher dispatcher)
    {
        _actions = actions;
        _session = session;
        _time = time;
        _dispatcher = dispatcher;
    }

    private CardRequestNotice() => _time = TimeProvider.System;

    /// <summary>One that never says anything: a page made without card requests.</summary>
    public static CardRequestNotice None => new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    public partial string Text { get; private set; } = "";

    public bool HasText => Text.Length > 0;

    /// <summary>Follows the cards' requests while the page shows.</summary>
    public void Attach()
    {
        if (_actions is null || _attached)
        {
            return;
        }
        _attached = true;
        _actions.RequestAnswered += OnAnswered;
        _session!.Changed += OnSessionChanged;
    }

    /// <summary>Stops following when the page goes, and says nothing more.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }
        _attached = false;
        _actions!.RequestAnswered -= OnAnswered;
        _session!.Changed -= OnSessionChanged;
        Clear();
    }

    /// <summary>The page read its titles again: what failed before shows on them as it stands now.</summary>
    public void Clear()
    {
        _timer?.Dispose();
        _timer = null;
        _failedCard = null;
        Text = "";
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher!.Post(Clear);

    // From the thread the answer came on, here on the page's.
    private void OnAnswered(object? sender, CardRequestAnswer answer) => _dispatcher!.Post(() => Show(answer));

    private void Show(CardRequestAnswer answer)
    {
        if (!_attached)
        {
            return;
        }
        if (answer.Succeeded)
        {
            if (answer.CardId == _failedCard)
            {
                Clear();
            }
            return;
        }
        _timer?.Dispose();
        int shown = ++_shown;
        _failedCard = answer.CardId;
        if (Text.Length > 0)
        {
            // The same words again are said again: gone first, back on the next turn, so screen readers hear a change.
            Text = "";
            _dispatcher!.Post(() =>
            {
                if (shown == _shown)
                {
                    Text = answer.Text;
                }
            });
        }
        else
        {
            Text = answer.Text;
        }
        _timer = _time.CreateTimer(_ => _dispatcher!.Post(() =>
        {
            if (shown == _shown)
            {
                Clear();
            }
        }), null, ShowTime, Timeout.InfiniteTimeSpan);
    }
}
