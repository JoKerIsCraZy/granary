using Granary.Core.ViewModels.Collections;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Granary.App.Controls;

/// <summary>"Add to a collection", shown in a dialog from a title's More actions.</summary>
public sealed partial class AddToCollectionPanel : UserControl
{
    private const int PosterWidth = 28;

    public AddToCollectionPanel(AddToCollectionViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public AddToCollectionViewModel ViewModel { get; }

    // Static because x:Bind function bindings in the row template call them on the type.
    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterWidth);

    public static bool Not(bool value) => !value;
}
