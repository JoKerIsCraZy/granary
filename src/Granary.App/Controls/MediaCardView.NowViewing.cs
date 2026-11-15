using System.Numerics;
using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Granary.App.Controls;

// The page's own title among its siblings (MediaCard.IsCurrent). An episode as the web's EpisodeCarousel marks it: a 2 px
// accent ring round the artwork and the "Now Viewing" pill top left, whose dot pings (Tailwind's animate-ping). A movie in
// its box set's row as the web's MovieBoxSetRow: a light ring and "This film" at the poster's foot. Only that card makes
// them; a recycled card changes the mark only when it changes.
public sealed partial class MediaCardView
{
    // animate-ping: the dot's copy grows to twice its size and fades out from 60 %, settling at three quarters of each second.
    private const float DotSize = 6;
    private const float PingScale = 2;
    private const float PingOpacity = 0.6f;
    private const float PingSettles = 0.75f;
    private static readonly TimeSpan PingDuration = TimeSpan.FromSeconds(1);

    // This film's pill: the web's place (its 19 px with its top 32 px above the foot, inside the two rings' pixels), and its
    // gap above the bottom badges when the card shows any.
    private const double ThisFilmFoot = 11;
    private const double ThisFilmGap = 4;

    // Windows' "Animation effects", read once when the app starts: turned off, the dot stands still.
    private static readonly bool Pings = new UISettings().AnimationsEnabled;

    // How the card is marked as its page's own title.
    private CurrentMark _mark;
    private bool _parked;
    private bool _loaded;
    private bool _pinging;
    // The artwork's own hairline, kept when the first mark colours it.
    private Brush? _artBorder;
    private Vector3KeyFrameAnimation? _pingGrow;
    private ScalarKeyFrameAnimation? _pingFade;

    /// <summary>
    /// Whether the card's list holds it back for reuse, still in the tree but out of view (a repeater's recycled card): its
    /// Now Viewing dot stops until the list shows the card again.
    /// </summary>
    public bool IsParked
    {
        get => _parked;
        set
        {
            if (_parked != value)
            {
                _parked = value;
                UpdatePing();
            }
        }
    }

    // The ring is the hairline in the mark's colour, with the pixel inside it the mark's own; a brush changes no layout. Now
    // Viewing's corner badges give way to its pill, as the web's carousel card has none there; This film's pill is at the foot.
    private void ShowMark(MediaCard? card)
    {
        var mark = card?.CurrentMark ?? CurrentMark.None;
        if (mark != _mark)
        {
            _mark = mark;
            _artBorder ??= Art.BorderBrush;
            if (mark == CurrentMark.NowViewing && NowViewing is null)
            {
                FindName(nameof(NowViewing));
                NowViewingLabel.Text = NowViewingText;
            }
            if (mark == CurrentMark.ThisFilm && ThisFilm is null)
            {
                FindName(nameof(ThisFilm));
                ThisFilmLabel.Text = ThisFilmText;
                // The bottom badges' height is known only once they are laid out.
                BottomBadges.SizeChanged += (_, _) => PlaceThisFilm();
            }
            Art.BorderBrush = mark switch
            {
                CurrentMark.NowViewing => Brush("SiloPrimaryBrush"),
                CurrentMark.ThisFilm => Brush("SiloThisFilmRingBrush"),
                _ => _artBorder,
            };
            NowViewing?.Visibility = Visible(mark == CurrentMark.NowViewing);
            ThisFilm?.Visibility = Visible(mark == CurrentMark.ThisFilm);
            TopLeftBadges.Visibility = Visible(mark != CurrentMark.NowViewing);
        }
        PlaceThisFilm();
        UpdatePing();
    }

    // A departure from the web: its MovieBoxSetRow puts the pill's top 2rem above the poster's foot, which is where Granary's
    // bottom badges sit (the profile's IMDb rating, say), and over them the card read "IMDB. THIS FILM". So while the card
    // shows bottom badges, the pill stands just above them; without any, at the web's place.
    private void PlaceThisFilm()
    {
        if (_mark != CurrentMark.ThisFilm || ThisFilmPill is null)
        {
            return;
        }
        bool hasBottomBadges = _cornerViews[2].Count + _cornerViews[3].Count > 0;
        // The badges' top above the art's inner foot, less the ring's pixel the pill sits inside of.
        double overBadges = BottomBadges.Margin.Bottom + BottomBadges.ActualHeight * BottomScale.ScaleY + ThisFilmGap
            - ThisFilm!.BorderThickness.Bottom;
        double bottom = hasBottomBadges ? Math.Max(ThisFilmFoot, overBadges) : ThisFilmFoot;
        if (ThisFilmPill.Margin.Bottom != bottom)
        {
            ThisFilmPill.Margin = new Thickness(0, 0, 0, bottom);
        }
    }

    // The dot pings on the compositor while its card shows: stopped for a card unloaded, parked or given another title, and
    // set back to its first frame, so the next start begins clean.
    private void UpdatePing()
    {
        bool pinging = _mark == CurrentMark.NowViewing && _loaded && !_parked && Pings;
        if (pinging == _pinging || NowViewingPing is null)
        {
            return;
        }
        _pinging = pinging;
        var visual = ElementCompositionPreview.GetElementVisual(NowViewingPing);
        if (!pinging)
        {
            visual.StopAnimation(nameof(Visual.Scale));
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Scale = Vector3.One;
            visual.Opacity = PingOpacity;
            return;
        }
        if (_pingGrow is null || _pingFade is null)
        {
            var compositor = visual.Compositor;
            // Tailwind's ease-out for ping: cubic-bezier(0, 0, 0.2, 1).
            var ease = compositor.CreateCubicBezierEasingFunction(Vector2.Zero, new Vector2(0.2f, 1));
            var grown = new Vector3(PingScale, PingScale, 1);
            _pingGrow = compositor.CreateVector3KeyFrameAnimation();
            _pingGrow.InsertKeyFrame(0, Vector3.One);
            _pingGrow.InsertKeyFrame(PingSettles, grown, ease);
            _pingGrow.InsertKeyFrame(1, grown);
            _pingFade = compositor.CreateScalarKeyFrameAnimation();
            _pingFade.InsertKeyFrame(0, PingOpacity);
            _pingFade.InsertKeyFrame(PingSettles, 0, ease);
            _pingFade.InsertKeyFrame(1, 0);
            _pingGrow.Duration = _pingFade.Duration = PingDuration;
            _pingGrow.IterationBehavior = _pingFade.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        visual.CenterPoint = new Vector3(DotSize / 2, DotSize / 2, 0);
        visual.StartAnimation(nameof(Visual.Scale), _pingGrow);
        visual.StartAnimation(nameof(Visual.Opacity), _pingFade);
    }
}
