using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// Notices from the server or its administrator (the control lane), as the web player shows them: below the top bar for
// eight seconds, a newer one in place of the last; a minimized player keeps one until the viewer can see it.
public sealed partial class PlayerViewModel
{
    /// <summary>How long a notice shows.</summary>
    public static readonly TimeSpan NoticeShowsFor = TimeSpan.FromSeconds(8);

    private ITimer? _noticeTimer;
    private bool _noticesHeld;

    /// <summary>The notice that shows; null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice), nameof(NoticeTitle), nameof(NoticeMessage), nameof(IsNoticeWarning))]
    public partial PlaybackNotice? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    public string NoticeTitle => Notice?.Title ?? "";

    public string NoticeMessage => Notice?.Message ?? "";

    /// <summary>Amber rather than blue: the server is going away.</summary>
    public bool IsNoticeWarning => Notice?.IsWarning == true;

    /// <summary>
    /// The player is out of sight (minimized to the bar) or back: a notice waits while it is, and its eight seconds start
    /// once the viewer sees it.
    /// </summary>
    public void HoldNotices(bool held)
    {
        _noticesHeld = held;
        if (held)
        {
            StopNoticeTimer();
        }
        else if (Notice is not null)
        {
            StartNoticeTimer();
        }
    }

    [RelayCommand]
    private void DismissNotice()
    {
        StopNoticeTimer();
        Notice = null;
    }

    private void OnNoticeRaised(object? sender, PlaybackNotice notice)
    {
        if (!ReferenceEquals(sender, _playback))
        {
            return;
        }
        Notice = notice;
        StopNoticeTimer();
        if (!_noticesHeld)
        {
            StartNoticeTimer();
        }
    }

    private void StartNoticeTimer()
    {
        var shown = Notice;
        _noticeTimer = _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            // A newer notice keeps its own time.
            if (ReferenceEquals(Notice, shown))
            {
                DismissNotice();
            }
        }), null, NoticeShowsFor, Timeout.InfiniteTimeSpan);
    }

    private void StopNoticeTimer()
    {
        _noticeTimer?.Dispose();
        _noticeTimer = null;
    }

    private void ResetNotice()
    {
        StopNoticeTimer();
        Notice = null;
    }
}
