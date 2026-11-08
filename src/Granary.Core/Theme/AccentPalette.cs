using System.Globalization;

namespace Granary.Core.Theme;

/// <summary>An opaque colour.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    /// <summary>"#ef4444".</summary>
    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>Part of the way to <paramref name="other"/>: 0 is this colour, 1 the other.</summary>
    public RgbColor Mix(RgbColor other, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        byte Blend(byte from, byte to) => (byte)Math.Round(from + (to - from) * t);
        return new(Blend(R, other.R), Blend(G, other.G), Blend(B, other.B));
    }

    // WCAG's relative luminance.
    public double Luminance => 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// The accent Granary draws its primary actions, focus and selection in, as the web paints Cinema Dark's primary, ring and
/// sidebar primary: the server's (its admin's branding accent, <c>lib/accentColor.ts</c>), or without one Cinema Dark's own
/// near-white primary (<c>app.css</c>, <c>--primary: #e8e8ec</c>). Hover and press are the accent seen through over the page,
/// as the web's <c>hover:bg-primary/90</c>, press a step further. A label on an accent fill is white or the dark label,
/// whichever contrasts more, as the web chooses. Destructive actions and errors are not drawn in it: they stay red.
/// </summary>
public sealed record AccentPalette(RgbColor Primary, RgbColor Hover, RgbColor Pressed, RgbColor Label)
{
    /// <summary>Cinema Dark's label on primary surfaces (<c>--primary-foreground</c>).</summary>
    public static readonly RgbColor DarkLabel = new(0x14, 0x14, 0x17);

    public static readonly RgbColor White = new(0xff, 0xff, 0xff);

    /// <summary>The page Granary draws on (Themes/Silo.xaml, <c>SiloBackgroundColor</c>), which a hovered accent lets through.</summary>
    public static readonly RgbColor Page = new(0x0e, 0x0e, 0x10);

    /// <summary>Cinema Dark's own primary, for a server without an accent and for no server at all, as the web's sign-in.</summary>
    public static AccentPalette Default { get; } = Of(new RgbColor(0xe8, 0xe8, 0xec));

    public bool IsDefault => this == Default;

    /// <summary>The palette of a stored accent; <see cref="Default"/> for none, or anything but a hex colour.</summary>
    public static AccentPalette From(string? accent) => Parse(accent) is { } primary ? Of(primary) : Default;

    private static AccentPalette Of(RgbColor primary) =>
        new(primary, primary.Mix(Page, 0.1), primary.Mix(Page, 0.2), PrefersWhiteLabel(primary) ? White : DarkLabel);

    /// <summary>"#rgb" or "#rrggbb", any case and with spaces around; null for anything else.</summary>
    public static RgbColor? Parse(string? accent)
    {
        string text = accent?.Trim() ?? "";
        if (text is not ['#', .. var digits] || digits.Length is not (3 or 6) || !digits.All(char.IsAsciiHexDigit))
        {
            return null;
        }
        if (digits.Length == 3)
        {
            digits = string.Concat(digits.Select(digit => $"{digit}{digit}"));
        }
        uint value = uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    // Whether white has the higher contrast on the accent than the dark label (WCAG ratios), as the web and its emails decide.
    private static bool PrefersWhiteLabel(RgbColor accent)
    {
        double background = accent.Luminance;
        double dark = DarkLabel.Luminance;
        return (1 + 0.05) / (background + 0.05) > (background + 0.05) / (dark + 0.05);
    }
}
