using System.Globalization;
using Granary.Core.Api.Images;
using Granary.Core.Api.Profiles;

namespace Granary.Core.ViewModels;

/// <summary>One tile of the profile picker.</summary>
/// <param name="AvatarUrl">Absolute and presigned: load it without credentials. Null shows <see cref="Initials"/>.</param>
public sealed record ProfileItemViewModel(string Id, string Name, Uri? AvatarUrl, bool HasPin, bool IsPrimary, bool IsChild)
{
    private const int MaximumInitials = 2;

    /// <summary>The first letters of the first two words of the name, upper-case.</summary>
    public string Initials => InitialsOf(Name);

    /// <summary>What a screen reader announces for the tile.</summary>
    public string AccessibleName => HasPin ? $"{Name}, protected by a PIN" : Name;

    public static ProfileItemViewModel From(Profile profile, Uri serverBase) =>
        new(profile.Id, profile.Name, ResolveAvatarUrl(profile.AvatarUrl, serverBase), profile.HasPin, profile.IsPrimary, profile.IsChild);

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
