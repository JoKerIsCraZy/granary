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

    private static void OnPersonChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var card = (PersonCreditCard)sender;
        var person = (DetailPerson?)e.NewValue;
        // Containers are recycled: the previous person's photo must not show under the new name. Set here,
        // not by an x:Bind function: those are skipped when their argument is null.
        card.InitialsText.Visibility = Visibility.Visible;
        card.Photo.Source = Artwork.Load(person?.PhotoUrl, PhotoDecodeWidth);
        card.RoleText.Visibility = string.IsNullOrEmpty(person?.Role) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPhotoOpened(object sender, RoutedEventArgs e) => InitialsText.Visibility = Visibility.Collapsed;

    private void OnPhotoFailed(object sender, ExceptionRoutedEventArgs e) => Photo.Source = null;
}
