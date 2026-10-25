using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels;

// The next episode (docs/api-contract.md, 10.7; NextUpFlow): the card at the credits, the "Playing Next" screen before
// the end with On Deck under it, and the countdown at the end.
public sealed partial class PlayerViewModel
{
    // The countdown's ring moves this often.
    private static readonly TimeSpan CountdownTick = TimeSpan.FromMilliseconds(100);

    private NextUpFlow _nextUp = new();
    private bool _nextUpStarted;
    private bool _onDeckRequested;
    private CancellationTokenSource? _nextUpLoad;
    private ITimer? _countdown;

    [ObservableProperty]
    public partial bool IsNextUpCardShown { get; private set; }

    [ObservableProperty]
    public partial bool IsNextUpScreenShown { get; private set; }

    /// <summary>"UP NEXT IN 31S", or "UP NEXT" without a countdown.</summary>
    [ObservableProperty]
    public partial string NextUpCardHeading { get; private set; } = "";

    /// <summary>"S1:E4 — Schrei der Verlorenen".</summary>
    [ObservableProperty]
    public partial string NextUpEpisodeText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasNextEpisode { get; private set; }

    [ObservableProperty]
    public partial string NextUpSeriesTitle { get; private set; } = "";

    /// <summary>"S1:E4".</summary>
    [ObservableProperty]
    public partial string NextUpCode { get; private set; } = "";

    [ObservableProperty]
    public partial string NextUpTitle { get; private set; } = "";

    /// <summary>"11 January 2024 • 37m".</summary>
    [ObservableProperty]
    public partial string NextUpMeta { get; private set; } = "";

    [ObservableProperty]
    public partial string NextUpOverview { get; private set; } = "";

    [ObservableProperty]
    public partial Uri? NextUpStill { get; private set; }

    /// <summary>"You've finished ted", when no episode follows.</summary>
    [ObservableProperty]
    public partial string NextUpFinishedHeading { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsNextUpCountingDown { get; private set; }

    /// <summary>"8s".</summary>
    [ObservableProperty]
    public partial string NextUpSecondsText { get; private set; } = "";

    /// <summary>1 to 0 while the countdown runs, for the ring.</summary>
    [ObservableProperty]
    public partial double NextUpCountdownLeft { get; private set; }

    /// <summary>"Auto-play is on".</summary>
    [ObservableProperty]
    public partial string AutoPlayText { get; private set; } = "";

    /// <summary>The profile's Continue Watching, without this series.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> OnDeck { get; private set; } = [];

    private TimeSpan Now => _time.GetElapsedTime(_created);

    /// <summary>Play Now, on the card and on the screen (Enter on the screen): the viewer's own choice.</summary>
    [RelayCommand]
    private void PlayNextNow()
    {
        _streak.Reset();
        PlayNext();
    }

    /// <summary>Cancel on the card.</summary>
    [RelayCommand]
    private void CancelNextUp()
    {
        _nextUp.Dismiss(Now);
        ShowNextUp();
    }

    /// <summary>The video in the screen's corner: back to it until the end, which then waits for the viewer.</summary>
    [RelayCommand]
    private void KeepWatching()
    {
        if (!_nextUp.HasEnded)
        {
            _streak.Reset();
            _nextUp.Dismiss(Now);
            ShowNextUp();
        }
    }

    [RelayCommand]
    private void ToggleAutoPlay()
    {
        _streak.Reset();
        bool enabled = !_nextUp.AutoPlayNext;
        _playback?.SetAutoPlayNext(enabled);
        _nextUp.SetAutoPlayNext(enabled, Now);
        ShowNextUp();
    }

    /// <summary>A card of On Deck: a movie or episode plays from where the profile left it, anything else opens.</summary>
    [RelayCommand]
    private Task PlayOnDeckAsync(MediaCard? card)
    {
        if (card is null)
        {
            return Task.CompletedTask;
        }
        _streak.Reset();
        if (card.Type is CatalogItemTypes.Movie or CatalogItemTypes.Episode)
        {
            return StartAsync(PlayRequest.Resume(card.Id));
        }
        Leave();
        _navigation.NavigateTo(AppRoute.Item, card.Id);
        return Task.CompletedTask;
    }

    // A new start: nothing of the title before carries over.
    private void ResetNextUp()
    {
        StopCountdown();
        _nextUpLoad?.Cancel();
        _nextUpLoad?.Dispose();
        _nextUpLoad = null;
        _nextUp = new NextUpFlow();
        ResetPreviousEpisode();
        _nextUpStarted = false;
        _onDeckRequested = false;
        OnDeck = [];
        ShowNextUp();
    }

    // Once per playback, when it first plays: its settings and title are known by then.
    private void BeginNextUp(IServerPlayback playback)
    {
        if (_nextUpStarted)
        {
            return;
        }
        _nextUpStarted = true;
        var episode = playback.Episode;
        _nextUp.Start(episode is not null, playback.Preferences.Markers, _streak.IsGated);
        TakeCredits(Markers);
        if (episode is null || _upNext is null)
        {
            _nextUp.TakeNext(null, Now);
            return;
        }
        _nextUpLoad = new CancellationTokenSource();
        _ = ResolveNextAsync(_upNext, episode, _nextUpLoad.Token);
        _ = ResolvePreviousAsync(_upNext, episode, _nextUpLoad.Token);
    }

    private async Task ResolveNextAsync(IUpNextSource source, PlayingEpisode episode, CancellationToken cancellationToken)
    {
        NextEpisode? next;
        try
        {
            next = await source.NextEpisodeAsync(episode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        _dispatcher.Post(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _nextUp.TakeNext(next, Now);
                ShowNextUp();
            }
        });
    }

    private void TakeCredits(IReadOnlyList<PlaybackMarker> markers) =>
        _nextUp.TakeCredits(markers.LastOrDefault(m => m.Kind == Api.Playback.WatchMarkerKinds.Credits)?.StartSeconds);

    private void FollowNextUp()
    {
        if (_playback is not null && CanSeek && Phase == PlayerPhase.Playing)
        {
            _nextUp.Update(PositionSeconds, DurationSeconds, Now);
            ShowNextUp();
        }
    }

    private void EndNextUp()
    {
        var step = _nextUp.End(Now);
        ShowNextUp();
        if (step == NextUpStep.Leave)
        {
            CloseCommand.Execute(null);
        }
    }

    private void OnCountdownTick()
    {
        if (_nextUp.Tick(Now) == NextUpStep.PlayNext)
        {
            // Nobody pressed Play: it counts towards the pass-out gate.
            _streak.RecordAutomatic();
            PlayNext();
            return;
        }
        ShowNextUp();
    }

    // The streak is the caller's: Play Now resets it, the countdown counts one up.
    private void PlayNext()
    {
        if (_nextUp.Next is { } next)
        {
            StopCountdown();
            _ = PlayAsync(PlayRequest.FromStart(next.ContentId));
        }
    }

    private void ShowNextUp()
    {
        var view = _nextUp.View;
        var next = _nextUp.Next;
        HasNextEpisode = next is not null;
        if (next is not null)
        {
            string code = string.Create(CultureInfo.InvariantCulture, $"S{next.SeasonNumber}:E{next.EpisodeNumber}");
            NextUpCode = code;
            NextUpTitle = next.Title;
            NextUpEpisodeText = string.IsNullOrWhiteSpace(next.Title) ? code : $"{code} — {next.Title}";
            NextUpMeta = string.Join(" • ", new[] { LongDate(next.AirDate), DetailText.Runtime(next.RuntimeMinutes) }.OfType<string>());
            NextUpOverview = next.Overview ?? "";
            NextUpStill = next.StillUrl;
        }
        NextUpSeriesTitle = _playback?.Episode?.SeriesTitle ?? "";
        NextUpFinishedHeading = NextUpSeriesTitle.Length > 0 ? $"You've finished {NextUpSeriesTitle}" : "End of playback";
        NextUpCardHeading = _nextUp.Seconds is { } seconds && view == NextUpView.Card
            ? string.Create(CultureInfo.InvariantCulture, $"UP NEXT IN {seconds}S")
            : "UP NEXT";
        IsNextUpCountingDown = view == NextUpView.Screen && _nextUp.IsCountingDown;
        NextUpSecondsText = _nextUp.Seconds is { } left ? string.Create(CultureInfo.InvariantCulture, $"{left}s") : "";
        NextUpCountdownLeft = _nextUp.CountdownLeft;
        AutoPlayText = _nextUp.AutoPlayNext ? "Auto-play is on" : "Auto-play is off";
        IsNextUpCardShown = view == NextUpView.Card;
        IsNextUpScreenShown = view == NextUpView.Screen;
        if (view == NextUpView.Screen)
        {
            LoadOnDeck();
        }
        if (_nextUp.IsCountingDown)
        {
            StartCountdown();
        }
        else
        {
            StopCountdown();
        }
        // The skip pill gives way to the card and the screen, as on the web player.
        ShowSkipPrompt();
    }

    private void LoadOnDeck()
    {
        if (_onDeckRequested || _upNext is null || _nextUpLoad is null)
        {
            return;
        }
        _onDeckRequested = true;
        var source = _upNext;
        var token = _nextUpLoad.Token;
        string? series = _playback?.Episode?.SeriesId;
        _ = LoadAsync();

        async Task LoadAsync()
        {
            IReadOnlyList<MediaCard> cards;
            try
            {
                cards = await source.OnDeckAsync(series, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            _dispatcher.Post(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    OnDeck = cards;
                }
            });
        }
    }

    private void StartCountdown() =>
        _countdown ??= _time.CreateTimer(_ => _dispatcher.Post(OnCountdownTick), null, CountdownTick, CountdownTick);

    private void StopCountdown()
    {
        _countdown?.Dispose();
        _countdown = null;
    }

    // "11 January 2024", as the web player's screen writes the air date.
    private static string? LongDate(string? isoDate) =>
        DateOnly.TryParseExact(isoDate?.Length >= 10 ? isoDate[..10] : isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : null;
}
