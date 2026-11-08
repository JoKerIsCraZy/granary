using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

// A box set's card (MediaCard.BoxSetFilms), as the web's BoxSetCard marks it apart from a film's: the sleeves above the poster,
// the BOX SET pill and the film count. Only a box set card makes them; a recycled card changes them only when it changes kind.
public sealed partial class MediaCardView
{
    // The poster moves down this far for the sleeves above it (the web's pt-1.5).
    private const double SleeveRoom = 6;

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
        }
        if (isBoxSet != _isBoxSet)
        {
            _isBoxSet = isBoxSet;
            Art.Margin = new Thickness(0, isBoxSet ? SleeveRoom : 0, 0, 0);
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
