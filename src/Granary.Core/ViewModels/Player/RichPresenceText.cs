using System.Globalization;
using System.Text;

namespace Granary.Core.ViewModels.Player;

/// <summary>A line of the Discord status within what Discord takes: 2 to 128 bytes of UTF-8, or it refuses the whole status.</summary>
internal static class RichPresenceText
{
    public const int MaxBytes = 128;

    private const string Ellipsis = "…";

    // Pads a one-character line: it does not show, and nothing trims it away as a space.
    private const char Pad = '​';

    /// <summary>
    /// <paramref name="text"/> without the spaces around it and with <paramref name="suffix"/> after it, the text cut after a
    /// whole character with an ellipsis where both would be too long, and padded when the line is a single character; null
    /// when there is no text.
    /// </summary>
    public static string? Fit(string? text, string suffix = "")
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        int room = MaxBytes - Encoding.UTF8.GetByteCount(suffix);
        if (Encoding.UTF8.GetByteCount(text) > room)
        {
            text = Cut(text, room - Encoding.UTF8.GetByteCount(Ellipsis)).TrimEnd() + Ellipsis;
        }
        text += suffix;
        return text.Length < 2 ? text + Pad : text;
    }

    // The text's first whole characters (letters with their accents, emoji with their joiners) within the bytes.
    private static string Cut(string text, int maxBytes)
    {
        var kept = new StringBuilder();
        int bytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            bytes += Encoding.UTF8.GetByteCount(element);
            if (bytes > maxBytes)
            {
                break;
            }
            kept.Append(element);
        }
        return kept.ToString();
    }
}
