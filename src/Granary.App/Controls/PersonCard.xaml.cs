using Granary.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Granary.App.Controls;

public sealed partial class PersonCard : UserControl
{
    public static readonly DependencyProperty PersonProperty =
        DependencyProperty.Register(nameof(Person), typeof(SearchPerson), typeof(PersonCard), new PropertyMetadata(null, OnPersonChanged));

    // Decoded at twice the shown size (112), so photos stay sharp at 200 % scaling.
    private const int PhotoDecodeWidth = 224;

    public PersonCard()
    {
        InitializeComponent();
    }

    public SearchPerson? Person
    {
        get => (SearchPerson?)GetValue(PersonProperty);
        set => SetValue(PersonProperty, value);
    }

    private static void OnPersonChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((PersonCard)sender).ShowPhoto((SearchPerson?)e.NewValue);

    private void ShowPhoto(SearchPerson? person)
    {
        // Containers are recycled: never let the previous person's photo show under the new name.
        var photo = Artwork.Load(person?.PhotoUrl, PhotoDecodeWidth);
        if (photo is not null)
        {
            // A missing or expired photo URL leaves the initials in place.
            photo.ImageFailed += OnPhotoFailed;
        }
        Picture.ProfilePicture = photo;
    }

    private void OnPhotoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (ReferenceEquals(Picture.ProfilePicture, sender))
        {
            Picture.ProfilePicture = null;
        }
        ((BitmapImage)sender).ImageFailed -= OnPhotoFailed;
    }
}
