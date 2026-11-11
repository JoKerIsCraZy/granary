using System.Numerics;
using Granary.App.Controls;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Views;

// The Playing Next screen (docs/ui-design.md, Player): the video moves into a 320 px window in the top left corner,
// above the screen, and fades out once the episode has ended; the still sizes itself to the page as the web's does.
public sealed partial class PlayerPage
{
    private const double MiniPlayerWidth = 320;

    // The still: at most 38 % of the page high and 400 px, 16:9, within the column.
    private const double StillHeightShare = 0.38;
    private const double StillMaxHeight = 400;
    private const double ColumnWidth = 820 - 64;

    private static readonly SolidColorBrush MiniPlayerEdge = new(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    // An unwatched episode's still the profile hides: the web's scale-110 over its blur.
    private const float HiddenStillScale = 1.1f;

    // The blur of a hidden still on its way; another still, or a shown one, drops it.
    private CancellationTokenSource? _hiddenStill;
    // The still and whether it is blurred change together: they are shown once, after both, and not again while the same.
    private bool _stillQueued;
    private (Uri? Url, bool Hidden) _stillShown;

    // The screen is not for the mini view nor the bar: there the picture stays as it is, and the countdown runs on behind it.
    private bool ShowsNextUpScreen => ViewModel.IsNextUpScreenShown && !IsCompact;

    private void ShowNextUpScreen()
    {
        PlaceVideoFrame();
        ShowCornerButton();
        if (ShowsNextUpScreen)
        {
            SizeNextUpStill();
            // Enter plays the next episode; Tab goes on from there.
            DispatcherQueue.TryEnqueue(() => (ViewModel.HasNextEpisode ? (Control)PlayNowButton : FinishedBackButton).Focus(FocusState.Programmatic));
        }
        else
        {
            KeepFocusInPage();
        }
        FadeMiniPlayer();
    }

    private void FadeMiniPlayer() =>
        VideoFrame.Opacity = ShowsNextUpScreen && ViewModel.Phase == PlayerPhase.Ended ? 0 : 1;

    private void SizeNextUpStill()
    {
        if (!ShowsNextUpScreen || ActualWidth <= 0)
        {
            return;
        }
        double width = Math.Min(Math.Min(ActualHeight * StillHeightShare, StillMaxHeight) * 16 / 9, Math.Min(ColumnWidth, ActualWidth * 0.92));
        NextUpStillFrame.Width = width;
        NextUpStillFrame.Height = width * 9 / 16;
        NextUpStillImage.CenterPoint = new Vector3((float)(width / 2), (float)(width * 9 / 32), 0);
    }

    // The background is the web's: the still blurred and darkened, also for an episode not started. The still itself is blurred
    // for such an episode while the profile hides them (PlayerViewModel.NextUpBlursStill), never shown sharp first.
    private void QueueNextUpStill()
    {
        if (!_stillQueued)
        {
            _stillQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _stillQueued = false;
                ShowNextUpStill();
            });
        }
    }

    // The page left: a blur on its way is dropped, and the still shown again when it comes back.
    private void StopNextUpStill()
    {
        _hiddenStill?.Cancel();
        _hiddenStill?.Dispose();
        _hiddenStill = null;
        _stillShown = default;
    }

    private void ShowNextUpStill()
    {
        bool hidden = ViewModel.NextUpBlursStill && ViewModel.NextUpStill is not null;
        if ((ViewModel.NextUpStill, hidden) == _stillShown)
        {
            return;
        }
        _stillShown = (ViewModel.NextUpStill, hidden);
        _hiddenStill?.Cancel();
        _hiddenStill?.Dispose();
        _hiddenStill = null;
        NextUpBackdrop.Source = Artwork.Load(ViewModel.NextUpStill, 48);
        NextUpStillImage.Scale = hidden ? new Vector3(HiddenStillScale, HiddenStillScale, 1) : Vector3.One;
        if (hidden)
        {
            NextUpStillImage.Source = null;
            _hiddenStill = new CancellationTokenSource();
            _ = ShowHiddenStillAsync(ViewModel.NextUpStill!, _hiddenStill.Token);
            return;
        }
        NextUpStillImage.Source = Artwork.Load(ViewModel.NextUpStill, (int)ColumnWidth);
    }

    private async Task ShowHiddenStillAsync(Uri url, CancellationToken cancellationToken)
    {
        var picture = await SpoilerArtwork.LoadAsync(url, NextUpStillFrame.Width > 0 ? NextUpStillFrame.Width : ColumnWidth, cancellationToken);
        if (!cancellationToken.IsCancellationRequested && picture is not null)
        {
            NextUpStillImage.Source = picture;
        }
    }

    // Instance methods because x:Bind function bindings call them on the page.
    // The ring's dash is its circumference (2π·20 px) in stroke widths of 3 px; the offset hides what has run out.
    public double RingOffset(double left) => (1 - left) * 41.89;

    public Visibility HasCards(IReadOnlyList<MediaCard> cards) => cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // A corner's distance, and below the title bar's strip: the drag area in a window, Exit full screen's in full screen.
    public Thickness CornerInset(double inset) => new(inset, TitleBarHeight + 8, inset, 0);

    public Visibility ShowsMiniPlayer(bool isScreenShown, PlayerPhase phase, bool isCompact) =>
        isScreenShown && phase != PlayerPhase.Ended && !isCompact ? Visibility.Visible : Visibility.Collapsed;

    public Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
