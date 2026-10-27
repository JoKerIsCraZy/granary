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
/// The accent Granary draws its primary actions, focus and selection in: the server's (its admin's branding accent, as the
/// web paints it over Cinema Dark, <c>lib/accentColor.ts</c>), or Granary's own red. Hover and press are a lighter and a
/// darker shade of it. A label on an accent fill is white or the dark label, whichever contrasts more, as the web
/// chooses; Granary's own red keeps its exact shades and labels.
/// </summary>
public sealed record AccentPalette(RgbColor Primary, RgbColor Hover, RgbColor Pressed, RgbColor Label, RgbColor BadgeLabel)
{
    /// <summary>Cinema Dark's label on primary surfaces, the page background.</summary>
    public static readonly RgbColor DarkLabel = new(0x14, 0x14, 0x17);

    public static readonly RgbColor White = new(0xff, 0xff, 0xff);

    private static readonly RgbColor Black = new(0, 0, 0);

    /// <summary>Granary's own red, with the shades and labels it was drawn with: dark on buttons, white on small badges.</summary>
    public static AccentPalette Default { get; } = new(new(0xef, 0x44, 0x44), new(0xf2, 0x58, 0x58), new(0xdc, 0x2f, 0x2f), DarkLabel, White);

    public bool IsDefault => this == Default;

    /// <summary>The palette of a stored accent; <see cref="Default"/> for none, Granary's own red, or anything but a hex colour.</summary>
    public static AccentPalette From(string? accent)
    {
        if (Parse(accent) is not { } primary || primary == Default.Primary)
        {
            return Default;
        }
        var label = PrefersWhiteLabel(primary) ? White : DarkLabel;
        return new AccentPalette(primary, primary.Mix(White, 0.1), primary.Mix(Black, 0.1), label, label);
    }

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
