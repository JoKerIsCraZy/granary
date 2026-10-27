using System.Globalization;

namespace Granary.Core.Overlays;

/// <summary>A badge on a card: what it says (upper-cased, as the web's badges), its corner, icon and colour.</summary>
/// <param name="Text">Empty when the icon is the word itself ("ATMOS", "HDR10").</param>
public sealed record CardBadge(string Id, string Text, OverlayPosition Position, string? IconId, string? Accent);

/// <summary>An ARGB colour of a badge.</summary>
public readonly record struct BadgeColor(byte A, byte R, byte G, byte B)
{
    public static readonly BadgeColor Transparent = new(0, 0, 0, 0);

    // Halves round up, as a browser rounds a colour (0.7 alpha is 179, not 178).
    public static BadgeColor Rgba(byte r, byte g, byte b, double alpha) => new(Byte(Math.Clamp(alpha, 0, 1) * 255), r, g, b);

    public static BadgeColor? FromHex(string? hex)
    {
        if (hex is not ['#', .. var digits] || digits.Length != 6 || !digits.All(char.IsAsciiHexDigit))
        {
            return null;
        }
        uint value = uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>CSS's <c>color-mix(in srgb, accent p%, base)</c>: premultiplied, as browsers mix a colour with a see-through one.</summary>
    public static BadgeColor Mix(BadgeColor accent, double share, BadgeColor background)
    {
        double a1 = accent.A / 255.0 * share;
        double a2 = background.A / 255.0 * (1 - share);
        double alpha = a1 + a2;
        if (alpha <= 0)
        {
            return Transparent;
        }
        byte Channel(byte c1, byte c2) => Byte((c1 * a1 + c2 * a2) / alpha);
        return new(Byte(alpha * 255), Channel(accent.R, background.R), Channel(accent.G, background.G), Channel(accent.B, background.B));
    }

    private static byte Byte(double value) => (byte)Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>How a badge style draws, at the web's reference poster width of 185 px (<c>lib/overlays/presets.ts</c>).</summary>
/// <param name="CornerRadius">Null for a full pill.</param>
/// <param name="CharacterSpacing">XAML's thousandths of an em: 25 is Tailwind's tracking-wide, 100 tracking-widest.</param>
public sealed record OverlayPreset(
    OverlayPresetId Id,
    string Label,
    string Description,
    double FontSize,
    bool IsBold,
    int CharacterSpacing,
    double PaddingX,
    double PaddingY,
    double? CornerRadius,
    double BorderWidth,
    double StackGap,
    double IconSize,
    bool PreferIcon)
{
    /// <summary>The poster width the sizes are for; a poster scales them with its width, a wide card keeps them.</summary>
    public const double ReferenceWidth = 185;

    public static IReadOnlyList<OverlayPreset> All { get; } =
    [
        new(OverlayPresetId.Minimal, "Minimal", "Near-invisible. Tiny text, no background.", 9, false, 100, 4, 0, 4, 0, 2, 10, false),
        new(OverlayPresetId.Classic, "Classic", "Semi-transparent dark pill with a white border. The default.", 10, false, 25, 8, 2, null, 1, 4, 11, false),
        new(OverlayPresetId.Vibrant, "Vibrant", "Opaque, accent-colored badges. High contrast.", 10, true, 25, 8, 2, 6, 0, 4, 12, true),
        new(OverlayPresetId.Pill, "Pill", "Larger pill with more padding. Works well with icons.", 10, false, 25, 10, 4, null, 1, 4, 12, true),
        new(OverlayPresetId.Square, "Square", "Blocky, high-density. Plex-inspired.", 9, true, 100, 6, 2, 4, 0, 2, 10, false),
    ];

    public static OverlayPreset Of(OverlayPresetId id) => All.First(preset => preset.Id == id);

    private static readonly BadgeColor White = new(255, 255, 255, 255);
    private static readonly BadgeColor WhiteBorder = BadgeColor.Rgba(255, 255, 255, 0.15);

    /// <summary>A badge's fill, text and border in this style, with the badge's colour or without one.</summary>
    /// <returns>The left border only for Square with a colour; otherwise the border all round (none when transparent).</returns>
    public (BadgeColor Background, BadgeColor Foreground, BadgeColor Border, bool BorderLeftOnly) Colors(string? accent)
    {
        var color = BadgeColor.FromHex(accent);
        switch (Id)
        {
            case OverlayPresetId.Minimal:
                return (BadgeColor.Transparent, color ?? BadgeColor.Rgba(255, 255, 255, 0.85), BadgeColor.Transparent, false);
            case OverlayPresetId.Classic:
                var black = BadgeColor.Rgba(0, 0, 0, 0.6);
                return (color is { } c1 ? BadgeColor.Mix(c1, 0.28, black) : black, White, WhiteBorder, false);
            case OverlayPresetId.Vibrant:
                return color is { } c2
                    ? (c2, White, BadgeColor.Transparent, false)
                    : (BadgeColor.Rgba(220, 220, 220, 0.95), new BadgeColor(255, 0, 0, 0), BadgeColor.Transparent, false);
            case OverlayPresetId.Pill:
                var night = BadgeColor.Rgba(20, 20, 30, 0.7);
                return (color is { } c3 ? BadgeColor.Mix(c3, 0.2, night) : night, White, WhiteBorder, false);
            default:
                return (BadgeColor.Rgba(0, 0, 0, 0.8), color ?? White, color ?? BadgeColor.Transparent, color is not null);
        }
    }
}

/// <summary>Which badges a card shows, and where, as the web's <c>CardOverlays</c> resolves them.</summary>
public static class CardBadges
{
    /// <summary>A corner stacks at most this many; further ones in the order are left out rather than run into the next corner.</summary>
    public const int MaxPerCorner = 3;

    // Icons that spell their own word: a label saying the same is left out, or the badge reads "HDR10 HDR10".
    private static readonly Dictionary<string, string> Wordmarks = new(StringComparer.Ordinal)
    {
        ["hdr"] = "HDR",
        ["hdr10"] = "HDR10",
        ["atmos"] = "ATMOS",
        ["av1"] = "AV1",
    };

    /// <summary>The word an icon spells ("HDR10" for hdr10), drawn as heavy text where no glyph exists; null for other icons.</summary>
    public static string? WordmarkOf(string iconId) => Wordmarks.GetValueOrDefault(iconId);

    public static IReadOnlyList<CardBadge> Resolve(OverlayData data, CardOverlayPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(prefs);
        var preset = OverlayPreset.Of(prefs.Preset);
        var badges = new List<CardBadge>();
        foreach (var position in Enum.GetValues<OverlayPosition>())
        {
            badges.AddRange(prefs.OrderedFor(position)
                .Select(def => Badge(def, data, prefs.ItemOf(def.Id), preset, position))
                .OfType<CardBadge>()
                .Take(MaxPerCorner));
        }
        return badges;
    }

    private static CardBadge? Badge(OverlayDef def, OverlayData data, OverlayItemConfig item, OverlayPreset preset, OverlayPosition position)
    {
        if (def.GetValue(data) is not { Length: > 0 } label)
        {
            return null;
        }
        string? icon = def.GetIcon?.Invoke(data) ?? def.IconId;
        bool showsIcon = def.IconCapable && icon is not null && (item.ShowIcon ?? preset.PreferIcon);
        string? shownIcon = showsIcon ? icon : null;
        // The viewer's colour first; then one the data asks for (amber for a title that needs attention); then the badge's own.
        string? accent = item.AccentColor ?? def.GetAccent?.Invoke(data) ?? def.DefaultAccent;
        string text = label.Trim().ToUpperInvariant();
        if (shownIcon is not null && Wordmarks.TryGetValue(shownIcon, out string? mark) && mark == text)
        {
            text = "";
        }
        return new CardBadge(def.Id, text, position, shownIcon, accent);
    }
}
