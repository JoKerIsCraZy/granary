using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Granary.App.Controls;

/// <summary>
/// A corner radius 1 px smaller, for what fills the inside of a 1 px border: its corners then meet the border's, as the
/// Watch Party mode cards' hover always did (11 inside 12).
/// </summary>
public sealed partial class InsetCornerRadius : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is CornerRadius radius
            ? new CornerRadius(Inset(radius.TopLeft), Inset(radius.TopRight), Inset(radius.BottomRight), Inset(radius.BottomLeft))
            : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();

    private static double Inset(double radius) => Math.Max(0, radius - 1);
}
