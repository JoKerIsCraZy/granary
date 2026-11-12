using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

// A box set's card (MediaCard.BoxSetFilms), as the web's BoxSetCard marks it apart from a film's: the sleeves above the poster,
// the BOX SET pill and the film count. Only a box set card makes them; a recycled card changes them only when it changes kind.
// None of them changes the card's measure: the sleeves stand above its box and the marks lie on the poster, so a box set
// card is exactly as tall as a film card at the same width (MeasureOverride, UpdateSize), and a grid's rows stay even
// whichever kinds they hold.
public sealed partial class MediaCardView
{
    // The count's distance from the poster's foot, and over a watched bar, which it rises clear of.
    private const double CountInset = 10;
    private const double CountInsetOverBar = 16;

    private bool _isBoxSet;

    private void ShowBoxSet(MediaCard? card)
    {
        bool isBoxSet = card?.BoxSetFilms is not null;
        if (isBoxSet && BoxSetMarks is null)
        {
            FindName(nameof(Sleeves));
            FindName(nameof(BoxSetMarks));
            BoxSetText.Text = BoxSetLabel;
        }
        if (isBoxSet != _isBoxSet)
        {
            _isBoxSet = isBoxSet;
            Sleeves?.Visibility = Visible(isBoxSet);
            BoxSetMarks?.Visibility = Visible(isBoxSet);
        }
        if (isBoxSet)
        {
            BoxSetCountText!.Text = card!.BoxSetFilms;
            BoxSetCount!.Margin = new Thickness(0, 0, 8, card.Progress is > 0 ? CountInsetOverBar : CountInset);
        }
    }
}
