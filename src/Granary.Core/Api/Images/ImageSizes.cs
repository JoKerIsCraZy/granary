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
    /// (not laid out yet) is left out; larger widths are capped at <see cref="MaximumWidth"/>.
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
                parameters.Add(new(name, Math.Min(w, MaximumWidth).ToString(CultureInfo.InvariantCulture)));
            }
        }
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
