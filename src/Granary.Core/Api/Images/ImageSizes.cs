using System.Globalization;

namespace Granary.Core.Api.Images;

/// <summary>
/// The widths, in device pixels, a page draws each artwork slot at, and optionally a size class
/// (<see cref="ImageSizeClasses"/>). The server answers each slot with the narrowest cached size at
/// least that wide. A null slot keeps the server's default. Turn it into request parameters with
/// <see cref="For"/>, which keeps only what the server advertises.
/// </summary>
public sealed record ImageSizes(int? Poster = null, int? Backdrop = null, int? Logo = null, int? Still = null, int? Photo = null, string? SizeClass = null)
{
    /// <summary>The widest value the server accepts.</summary>
    public const int MaximumWidth = 7680;

    /// <summary>Poster 342, backdrop 1280, logo 500, still 400 and photo 185 pixels.</summary>
    public static ImageSizes Default { get; } = new(Poster: 342, Backdrop: 1280, Logo: 500, Still: 400, Photo: 185);

    /// <summary>
    /// How much narrower than the width drawn a picture may be, as the web's <c>RUNG_SLACK</c> (its
    /// <c>lib/artworkWidths.ts</c>): the server's sizes are far apart, and a few pixels past one would fetch the next at
    /// several times the bytes for nothing to see.
    /// </summary>
    public const double RungSlack = 0.05;

    /// <summary>
    /// The widest backdrop asked for on a hero's account: the widest size the server keeps (silo-server
    /// <c>internal/artworkkey/artworkkey.go</c>, <c>VariantWidths</c>). Past it the server answers the original, up to
    /// 3840 px wide and several times the bytes. The web takes that for its banner on a very wide window; Granary does
    /// not, because its hero shows the title of any card, so every row's backdrops come at the hero's width, the wide
    /// cards' among them.
    /// </summary>
    public const int WidestHeroBackdrop = 2560;

    /// <summary>
    /// The same sizes with backdrops as wide as a hero draws them, <paramref name="devicePixels"/>, less
    /// <see cref="RungSlack"/>; never narrower than they are asked for already, and <see cref="WidestHeroBackdrop"/> at
    /// most either way: at a display scale of 300 % the default width alone would fetch the original. A width not known
    /// (0, not a number) changes nothing.
    /// </summary>
    public ImageSizes WithDrawnBackdrop(double devicePixels)
    {
        if (!(devicePixels > 0) || !double.IsFinite(devicePixels))
        {
            return this;
        }
        int drawn = (int)Math.Ceiling(Math.Min(devicePixels, WidestHeroBackdrop) * (1 - RungSlack));
        return this with { Backdrop = Math.Min(Math.Max(Backdrop ?? 0, drawn), WidestHeroBackdrop) };
    }

    /// <summary>The same sizes for a display scale (for example 1.5 at 150 %), rounded to whole pixels.</summary>
    public ImageSizes ScaledBy(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be a positive number.");
        }
        return this with { Poster = Scale(Poster), Backdrop = Scale(Backdrop), Logo = Scale(Logo), Still = Scale(Still), Photo = Scale(Photo) };

        int? Scale(int? width) => width is { } w ? (int)Math.Round(w * scale, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>
    /// The query parameters for these sizes on the server that sent <paramref name="capabilities"/>:
    /// only the slots it lists in <c>slot_width_params</c> and a size class it lists, and nothing at all
    /// unless the document is <c>available</c> (an unadvertised parameter is a 422). A width of 0 or less
    /// (not laid out yet) is left out; larger widths are capped at <see cref="MaximumWidth"/>. A width past the widest
    /// size the server lists for its slot takes that size while it covers four fifths of it, as the web's
    /// <c>TOP_RUNG_COVERAGE</c> (its <c>lib/artworkWidths.ts</c>): past its widest size the server answers the original,
    /// up to 3840 px wide and several times the bytes.
    /// </summary>
    public ImageParameters For(ImageCapabilities? capabilities)
    {
        if (capabilities is not { IsAvailable: true })
        {
            return ImageParameters.None;
        }
        var parameters = new List<KeyValuePair<string, string>>();
        if (SizeClass is { Length: > 0 } sizeClass && capabilities.Param.Length > 0 && capabilities.Sizes.Contains(sizeClass, StringComparer.Ordinal))
        {
            parameters.Add(new(capabilities.Param, sizeClass));
        }
        AddWidth(ImageSlots.Poster, Poster);
        AddWidth(ImageSlots.Backdrop, Backdrop);
        AddWidth(ImageSlots.Logo, Logo);
        AddWidth(ImageSlots.Still, Still);
        AddWidth(ImageSlots.Photo, Photo);
        return parameters.Count == 0 ? ImageParameters.None : new ImageParameters(parameters);

        void AddWidth(string slot, int? width)
        {
            if (width is > 0 and var w && capabilities.SlotWidthParams.TryGetValue(slot, out string? name) && !string.IsNullOrEmpty(name))
            {
                parameters.Add(new(name, Math.Min(WithinTheSizes(slot, w), MaximumWidth).ToString(CultureInfo.InvariantCulture)));
            }
        }

        // The sizes of a cast or crew photo are listed as "profile", the others under their slot's name.
        int WithinTheSizes(string slot, int width) =>
            capabilities.Widths.TryGetValue(slot == ImageSlots.Photo ? "profile" : slot, out var sizes)
                && sizes is { Large: > 0 and var top } && width > top && top * 5L >= width * 4L
                ? top
                : width;
    }
}

/// <summary>
/// Image query parameters the server advertised, made by <see cref="ImageSizes.For"/>. Keep the same
/// instance across the pages of a listing: cursors are bound to the image parameters.
/// </summary>
public sealed class ImageParameters : IEquatable<ImageParameters>
{
    internal ImageParameters(IReadOnlyList<KeyValuePair<string, string>> values) => Values = values;

    /// <summary>No image parameters: every slot at the server's default size.</summary>
    public static ImageParameters None { get; } = new([]);

    public IReadOnlyList<KeyValuePair<string, string>> Values { get; }

    public bool IsEmpty => Values.Count == 0;

    public bool Equals(ImageParameters? other) => other is not null && Values.SequenceEqual(other.Values);

    public override bool Equals(object? obj) => Equals(obj as ImageParameters);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (name, value) in Values)
        {
            hash.Add(name);
            hash.Add(value);
        }
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join('&', Values.Select(v => $"{v.Key}={v.Value}"));
}
