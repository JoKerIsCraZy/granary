using System.Globalization;
using System.Text.Json;

namespace Granary.Core.Playback;

/// <summary>How the profile wants text subtitles to look (<c>playback.subtitle_appearance</c>, docs/api-contract.md 6.6).</summary>
public enum SubtitleBackground
{
    /// <summary>A box behind the text, in the background colour at its opacity.</summary>
    Box,

    /// <summary>A drop shadow in the outline colour.</summary>
    Shadow,

    /// <summary>An outline in the outline colour.</summary>
    Outline,

    None,
}

public enum SubtitlePosition
{
    Bottom,

    LowerThird,

    Top,
}

/// <summary>
/// The profile's subtitle style, as Silo's Android and Apple clients read it (SubtitleAppearance): sanitized, so a
/// broken value falls back to the default rather than to unreadable subtitles. Image subtitles and styled ASS keep
/// their own look.
/// </summary>
/// <param name="FontSize">At a picture height of 1080 lines: 36, 44, 56, 68 or 82 for small to extra-extra-large.</param>
/// <param name="FontFamily"><c>sans-serif</c>, <c>serif</c>, <c>monospace</c> or a font's name.</param>
/// <param name="FontColor">#RRGGBB.</param>
/// <param name="TextOpacity">1 to 100 percent.</param>
/// <param name="BackgroundColor">#RRGGBB, for the box.</param>
/// <param name="BackgroundOpacity">0 to 100 percent, for the box.</param>
/// <param name="TextOutline">An outline around the text whatever the background.</param>
/// <param name="TextOutlineColor">#RRGGBB, for outline and shadow.</param>
public sealed record SubtitleAppearance(
    double FontSize,
    string FontFamily,
    string FontColor,
    int TextOpacity,
    string BackgroundColor,
    SubtitleBackground Background,
    int BackgroundOpacity,
    bool TextOutline,
    string TextOutlineColor,
    SubtitlePosition Position)
{
    public const string SansSerif = "sans-serif";
    public const string Serif = "serif";
    public const string Monospace = "monospace";

    /// <summary>Large white text with a shadow at the bottom, the clients' default.</summary>
    public static SubtitleAppearance Default { get; } =
        new(56, SansSerif, "#ffffff", 100, "#000000", SubtitleBackground.Shadow, 75, false, "#000000", SubtitlePosition.Bottom);

    /// <summary>The style in a setting's value, the default for anything missing or broken.</summary>
    public static SubtitleAppearance From(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } style)
        {
            return Default;
        }
        var fallback = Default;
        return new SubtitleAppearance(
            Text(style, "fontSize") switch
            {
                "small" => 36,
                "medium" => 44,
                "large" => 56,
                "xlarge" => 68,
                "xxlarge" => 82,
                _ => fallback.FontSize,
            },
            Text(style, "fontFamily") is { Length: > 0 } family ? family : fallback.FontFamily,
            Color(style, "fontColor") ?? fallback.FontColor,
            Math.Clamp(Number(style, "textOpacity") ?? fallback.TextOpacity, 1, 100),
            Color(style, "backgroundColor") ?? fallback.BackgroundColor,
            Text(style, "backgroundStyle") switch
            {
                "box" => SubtitleBackground.Box,
                "outline" => SubtitleBackground.Outline,
                "none" => SubtitleBackground.None,
                _ => fallback.Background,
            },
            Math.Clamp(Number(style, "backgroundOpacity") ?? fallback.BackgroundOpacity, 0, 100),
            style.TryGetProperty("textOutline", out var outline) && outline.ValueKind == JsonValueKind.True,
            Color(style, "textOutlineColor") ?? fallback.TextOutlineColor,
            Text(style, "position") switch
            {
                "lower-third" => SubtitlePosition.LowerThird,
                "top" => SubtitlePosition.Top,
                _ => fallback.Position,
            });
    }

    private static string? Text(JsonElement style, string name) =>
        style.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static int? Number(JsonElement style, string name) =>
        style.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            && double.IsFinite(number)
            ? (int)Math.Round(number)
            : null;

    // #RRGGBB, with or without the hash; anything else is the default.
    private static string? Color(JsonElement style, string name)
    {
        string hex = Text(style, name)?.TrimStart('#') ?? "";
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? "#" + hex.ToLowerInvariant() : null;
    }
}
