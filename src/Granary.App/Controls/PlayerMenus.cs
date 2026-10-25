using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>
/// For the rows of the player's menus. Static, because x:Bind in a data template calls functions on a type, not on
/// the page.
/// </summary>
public static class PlayerMenus
{
    public static Visibility Shows(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    // Chips are upper case on screen, as on the web; the row's accessible name keeps the words as written. Codec
    // names are identifiers, not prose: "vorbis" stays VORBIS in every culture.
    public static string Upper(string text) => text.ToUpperInvariant();

    // The chosen row's name is blue, the others white.
    public static Brush RowForeground(bool isSelected) =>
        (Brush)Application.Current.Resources[isSelected ? "SiloPlayerMenuChosenBrush" : "SiloPlayerMenuRowBrush"];

    public static string Status(bool isSelected) => isSelected ? "Selected" : "";

    // "Subtitle delay +300 ms", read when it changes.
    public static string DelayName(string delay) => "Subtitle delay " + delay;

    // The subtitle menu marks its chosen row as the web does: a faint light behind it and white text, the others dimmed.
    public static Brush SubtitleRowBackground(bool isSelected) =>
        new SolidColorBrush(isSelected ? Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF) : Microsoft.UI.Colors.Transparent);

    public static Brush SubtitleRowForeground(bool isSelected) =>
        new SolidColorBrush(isSelected ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    // A chapter row: "Opening, 4 minutes 5 seconds", and where it starts as the web writes it ("4:05").
    public static string ChapterName(string title, double startSeconds) => $"{title}, {PlayerTime.Spoken(startSeconds)}";

    public static string ChapterStart(double startSeconds) => PlayerTime.Format(startSeconds);

    public static string ChapterStatus(bool isCurrent) => isCurrent ? "Playing" : "";

    // 80 by 48 on the row; a chapter without a picture keeps the placeholder.
    public static ImageSource? ChapterThumbnail(Uri? url) => Artwork.Load(url, 80);
}
