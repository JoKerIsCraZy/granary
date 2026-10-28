using System.Text.Json;

namespace Granary.Core.ViewModels.Cards;

/// <summary>How big posters are: the web's <c>poster_size</c>.</summary>
public enum PosterSize
{
    Compact,
    Standard,
    Large,
}

/// <summary>What is written under a poster: the web's <c>caption</c>, its title and details, the title only, or nothing.</summary>
public enum CardCaption
{
    TitleMetadata,
    Title,
    Artwork,
}

/// <summary>The web's breakpoints (Tailwind's <c>sm</c> 640, <c>md</c> 768, <c>lg</c> 1024 and <c>xl</c> 1280 px), of the window's width.</summary>
public enum WindowBreakpoint
{
    Base,
    Small,
    Medium,
    Large,
    ExtraLarge,
}

/// <summary>
/// The profile's poster size and caption, the web's <c>ui.card_presentation</c> (<c>lib/uiCustomization.ts</c>), and the sizes
/// they give cards there: a grid's columns and gap at each breakpoint, a row's card width. A value the web would not read is
/// the default, as there.
/// </summary>
public sealed record CardPresentation(PosterSize PosterSize, CardCaption Caption)
{
    public static CardPresentation Default { get; } = new(PosterSize.Standard, CardCaption.TitleMetadata);

    private static readonly (PosterSize Size, string Name)[] SizeNames =
        [(PosterSize.Compact, "compact"), (PosterSize.Standard, "standard"), (PosterSize.Large, "large")];

    private static readonly (CardCaption Caption, string Name)[] CaptionNames =
        [(CardCaption.TitleMetadata, "title_metadata"), (CardCaption.Title, "title"), (CardCaption.Artwork, "artwork")];

    // cardGridClasses, from the base breakpoint to xl.
    private static readonly int[] CompactColumns = [3, 5, 6, 8, 10];
    private static readonly int[] StandardColumns = [3, 4, 5, 7, 8];
    private static readonly int[] LargeColumns = [2, 3, 4, 5, 6];

    /// <summary>The title shows under the artwork.</summary>
    public bool ShowsCaption => Caption != CardCaption.Artwork;

    /// <summary>The details show under the title: the year, an episode's title and code, the time left.</summary>
    public bool ShowsDetails => Caption == CardCaption.TitleMetadata;

    /// <summary>The web's space between a grid's cards, across and down: <c>gap-3</c>, or <c>gap-4</c> for large posters.</summary>
    public double GridGap => PosterSize == PosterSize.Large ? 16 : 12;

    public static WindowBreakpoint BreakpointOf(double windowWidth) => windowWidth switch
    {
        >= 1280 => WindowBreakpoint.ExtraLarge,
        >= 1024 => WindowBreakpoint.Large,
        >= 768 => WindowBreakpoint.Medium,
        >= 640 => WindowBreakpoint.Small,
        _ => WindowBreakpoint.Base,
    };

    /// <summary>A grid's columns at <paramref name="at"/>, as the web's <c>cardGridClasses</c>.</summary>
    public int GridColumns(WindowBreakpoint at)
    {
        var columns = PosterSize switch
        {
            PosterSize.Compact => CompactColumns,
            PosterSize.Large => LargeColumns,
            _ => StandardColumns,
        };
        return columns[Math.Clamp((int)at, 0, columns.Length - 1)];
    }

    /// <summary>
    /// A grid's columns at <paramref name="at"/> in <paramref name="width"/>: the web's, and more where its posters would be wider
    /// than a row's. The web stops adding columns at <c>xl</c>, so on a wide or ultrawide window its posters keep growing; here
    /// a poster of the chosen size stays as big as it is in the rows.
    /// </summary>
    public int GridColumns(WindowBreakpoint at, double width)
    {
        int columns = GridColumns(at);
        if (!double.IsFinite(width) || width <= 0)
        {
            return columns;
        }
        double widest = RowCardWidth(MediaCardShape.Poster, at);
        int needed = (int)Math.Ceiling((width + GridGap) / (widest + GridGap));
        return Math.Max(columns, needed);
    }

    /// <summary>A row's card width at <paramref name="at"/>, as the web's <c>carouselCardWidthPx</c>; a wide card's is the same at every size.</summary>
    public double RowCardWidth(MediaCardShape shape, WindowBreakpoint at)
    {
        if (shape == MediaCardShape.Landscape)
        {
            return at >= WindowBreakpoint.Small ? 315 : 260;
        }
        var (narrow, small, large) = PosterSize switch
        {
            PosterSize.Compact => (120, 140, 160),
            PosterSize.Large => (170, 195, 220),
            _ => (140, 160, 185),
        };
        return at >= WindowBreakpoint.Large ? large : at >= WindowBreakpoint.Small ? small : narrow;
    }

    /// <summary>The setting's value: the default unless it is an object with a known <c>poster_size</c> and <c>caption</c>.</summary>
    public static CardPresentation Parse(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } json
            || NameIn(json, "poster_size") is not { } sizeName
            || NameIn(json, "caption") is not { } captionName)
        {
            return Default;
        }
        var size = SizeNames.Where(s => s.Name == sizeName).Select(s => (PosterSize?)s.Size).FirstOrDefault();
        var caption = CaptionNames.Where(c => c.Name == captionName).Select(c => (CardCaption?)c.Caption).FirstOrDefault();
        return size is { } knownSize && caption is { } knownCaption ? new CardPresentation(knownSize, knownCaption) : Default;
    }

    public JsonElement ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("poster_size", SizeNames.First(s => s.Size == PosterSize).Name);
            writer.WriteString("caption", CaptionNames.First(c => c.Caption == Caption).Name);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string? NameIn(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
