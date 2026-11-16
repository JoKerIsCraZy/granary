using System.Globalization;
using Granary.Core.Api.Images;
using Granary.Core.Api.Profiles;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>One tile of the profile picker.</summary>
/// <param name="AvatarUrl">Absolute and presigned: load it without credentials. Null shows <see cref="Initials"/>.</param>
public sealed record ProfileItemViewModel(string Id, string Name, Uri? AvatarUrl, bool HasPin, bool IsPrimary, bool IsChild)
{
    private const int MaximumInitials = 2;

    // avatar_source of a picture the server offers to pick, rather than one uploaded (docs/api-contract.md, 6.1).
    private const string PresetSource = "preset";

    /// <summary>The first letters of the first two words of the name, upper-case.</summary>
    public string Initials => InitialsOf(Name);

    /// <summary>What a screen reader announces for the tile: the name, and that it is PIN protected, as the web's picker.</summary>
    public string AccessibleName => HasPin ? AppText.Get("web.profiles.picker.pinProtected", ("name", Name)) : Name;

    /// <summary>
    /// The picture is one of the server's own presets (<c>avatar_source</c> <c>preset</c>, on the server's own origin): the only
    /// picture that may be drawn as SVG (<see cref="Granary.Core.Artwork.AvatarSvg"/>). A preset from another host, as DiceBear's, is not.
    /// </summary>
    public bool AvatarIsServerPreset { get; init; }

    public static ProfileItemViewModel From(Profile profile, Uri serverBase)
    {
        var avatarUrl = ResolveAvatarUrl(profile.AvatarUrl, serverBase);
        return new(profile.Id, profile.Name, avatarUrl, profile.HasPin, profile.IsPrimary, profile.IsChild)
        {
            AvatarIsServerPreset = string.Equals(profile.AvatarSource, PresetSource, StringComparison.Ordinal)
                && avatarUrl is not null
                && Uri.Compare(avatarUrl, serverBase, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0,
        };
    }

    /// <summary>Makes <c>avatar_url</c> absolute like every other image member (<see cref="ArtworkUrl.Resolve"/>).</summary>
    internal static Uri? ResolveAvatarUrl(string? avatarUrl, Uri serverBase) => ArtworkUrl.Resolve(serverBase, avatarUrl);

    internal static string InitialsOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }
        var words = name.Split((char[]?)null, MaximumInitials + 1, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var initials = words.Take(MaximumInitials).Select(word => StringInfo.GetNextTextElement(word));
        return string.Concat(initials).ToUpperInvariant();
    }

    public override string ToString() => AccessibleName;
}
