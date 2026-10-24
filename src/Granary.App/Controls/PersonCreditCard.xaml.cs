using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

public sealed partial class PersonCreditCard : UserControl
{
    public static readonly DependencyProperty PersonProperty =
        DependencyProperty.Register(nameof(Person), typeof(DetailPerson), typeof(PersonCreditCard), new PropertyMetadata(null, OnPersonChanged));

    // Twice the shown width, so photos stay sharp at 200 % scaling.
    private const int PhotoDecodeWidth = 240;

    public PersonCreditCard()
    {
        InitializeComponent();
    }

    public DetailPerson? Person
    {
        get => (DetailPerson?)GetValue(PersonProperty);
        set => SetValue(PersonProperty, value);
    }

    // Instance method because x:Bind function bindings call it on the control.
    public Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private static void OnPersonChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var card = (PersonCreditCard)sender;
        // Containers are recycled: the previous person's photo must not show under the new name.
        card.InitialsText.Visibility = Visibility.Visible;
        card.Photo.Source = Artwork.Load(((DetailPerson?)e.NewValue)?.PhotoUrl, PhotoDecodeWidth);
    }

    private void OnPhotoOpened(object sender, RoutedEventArgs e) => InitialsText.Visibility = Visibility.Collapsed;

    private void OnPhotoFailed(object sender, ExceptionRoutedEventArgs e) => Photo.Source = null;
}
