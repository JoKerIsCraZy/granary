using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// Notices from the server or its administrator (the control lane), as the web player shows them: below the top bar for
// eight seconds, a newer one in place of the last. Minimized to the bar, the window shows it above the bar; the mini view,
// too small for one, keeps it until the viewer can see it. A title Silo's phone app started says so in the same card, for
// four seconds (design 5.9).
public sealed partial class PlayerViewModel
{
    /// <summary>How long a notice shows.</summary>
    public static readonly TimeSpan NoticeShowsFor = TimeSpan.FromSeconds(8);

    /// <summary>How long the notice of a title a phone started shows, as long as the "connected" notice.</summary>
    public static readonly TimeSpan StartedFromPhoneShowsFor = TimeSpan.FromSeconds(4);

    private ITimer? _noticeTimer;
    private bool _noticesHeld;

    // Silo's cast_standby_starting and cast_standby_from (silo-android's strings_catalog.xml), which its TV apps show over
    // the player when a phone started a title.
    private const string StartingText = "Starting {0}";
    private const string FromText = "From {0}";

    // The request a phone started and the phone's name, until the title's details are in and the notice shows.
    private (PlayRequest Request, string Phone)? _startedFromPhone;

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
    /// The player has no room for a notice (the mini view) or has it again: a notice waits meanwhile, and its eight seconds
    /// start once the viewer sees it.
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

    /// <summary>
    /// A paired phone started <paramref name="request"/>: once its title is known, the notice says "Starting {title}" and
    /// "From {phone}", Silo's own words where its TV apps show who started a title (<c>cast_standby_starting</c>,
    /// <c>cast_standby_from</c>). Only that request's title says so, once.
    /// </summary>
    public void NoteStartedFromPhone(PlayRequest request, string phoneName)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(phoneName);
        _startedFromPhone = (request, phoneName);
        ShowStartedFromPhone();
    }

    partial void OnMediaInfoChanged(PlaybackMediaInfo value) => ShowStartedFromPhone();

    private void ShowStartedFromPhone()
    {
        if (_startedFromPhone is not var (request, phone))
        {
            return;
        }
        if (!ReferenceEquals(_request, request))
        {
            // Another title plays by now (the next episode): the phone did not start that one.
            _startedFromPhone = null;
            return;
        }
        if (MediaInfo.Title.Length == 0)
        {
            return;
        }
        _startedFromPhone = null;
        ShowNotice(new PlaybackNotice(string.Format(CultureInfo.CurrentCulture, StartingText, MediaInfo.Title),
            string.Format(CultureInfo.CurrentCulture, FromText, phone)) { ShowsFor = StartedFromPhoneShowsFor });
    }

    [RelayCommand]
    private void DismissNotice()
    {
        StopNoticeTimer();
        Notice = null;
    }

    private void OnNoticeRaised(object? sender, PlaybackNotice notice)
    {
        if (ReferenceEquals(sender, _playback))
        {
            ShowNotice(notice);
        }
    }

    // In place of the one that shows, for its own time from now on, or from when the viewer can see it.
    private void ShowNotice(PlaybackNotice notice)
    {
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
        }), null, shown?.ShowsFor ?? NoticeShowsFor, Timeout.InfiniteTimeSpan);
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
