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

    private void InitializeNextUp() =>
        RegisterPropertyChangedCallback(IsFullScreenProperty, (_, _) =>
        {
            if (ViewModel.IsNextUpScreenShown)
            {
                VideoFrame.Margin = CornerInset(IsFullScreen, 24);
            }
        });

    private void ShowNextUpScreen()
    {
        if (ViewModel.IsNextUpScreenShown)
        {
            VideoFrame.Width = MiniPlayerWidth;
            VideoFrame.Height = MiniPlayerWidth * 9 / 16;
            VideoFrame.HorizontalAlignment = HorizontalAlignment.Left;
            VideoFrame.VerticalAlignment = VerticalAlignment.Top;
            VideoFrame.Margin = CornerInset(IsFullScreen, 24);
            VideoFrame.CornerRadius = new CornerRadius(16);
            VideoFrame.BorderThickness = new Thickness(1);
            VideoFrame.BorderBrush = MiniPlayerEdge;
            Canvas.SetZIndex(VideoFrame, 2);
            SizeNextUpStill();
            // Enter plays the next episode; Tab goes on from there.
            DispatcherQueue.TryEnqueue(() => (ViewModel.HasNextEpisode ? (Control)PlayNowButton : FinishedBackButton).Focus(FocusState.Programmatic));
        }
        else
        {
            VideoFrame.Width = double.NaN;
            VideoFrame.Height = double.NaN;
            VideoFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
            VideoFrame.VerticalAlignment = VerticalAlignment.Stretch;
            VideoFrame.Margin = default;
            VideoFrame.CornerRadius = default;
            VideoFrame.BorderThickness = default;
            Canvas.SetZIndex(VideoFrame, 0);
            KeepFocusInPage();
        }
        FadeMiniPlayer();
    }

    private void FadeMiniPlayer() =>
        VideoFrame.Opacity = ViewModel.IsNextUpScreenShown && ViewModel.Phase == PlayerPhase.Ended ? 0 : 1;

    private void SizeNextUpStill()
    {
        if (!ViewModel.IsNextUpScreenShown || ActualWidth <= 0)
        {
            return;
        }
        double width = Math.Min(Math.Min(ActualHeight * StillHeightShare, StillMaxHeight) * 16 / 9, Math.Min(ColumnWidth, ActualWidth * 0.92));
        NextUpStillFrame.Width = width;
        NextUpStillFrame.Height = width * 9 / 16;
    }

    private void ShowNextUpStill()
    {
        NextUpStillImage.Source = Artwork.Load(ViewModel.NextUpStill, (int)ColumnWidth);
        NextUpBackdrop.Source = Artwork.Load(ViewModel.NextUpStill, 48);
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public string NextUpLabel(bool hasNextEpisode) => hasNextEpisode ? "PLAYING NEXT" : "FINISHED";

    // The ring's dash is its circumference (2π·20 px) in stroke widths of 3 px; the offset hides what has run out.
    public double RingOffset(double left) => (1 - left) * 41.89;

    public Visibility HasCards(IReadOnlyList<MediaCard> cards) => cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // A corner's distance, and below the title bar's drag area in a window.
    public Thickness CornerInset(bool isFullScreen, double inset) => new(inset, isFullScreen ? inset : TitleBarHeight + 8, inset, 0);

    public Visibility ShowsMiniPlayer(bool isScreenShown, PlayerPhase phase) =>
        isScreenShown && phase != PlayerPhase.Ended ? Visibility.Visible : Visibility.Collapsed;

    public Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
