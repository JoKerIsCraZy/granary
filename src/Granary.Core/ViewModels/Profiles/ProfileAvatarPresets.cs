using System.Globalization;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Profiles;

/// <summary>A DiceBear style the editor offers: its id, name and line, as the web's <c>PROFILE_AVATAR_STYLES</c>.</summary>
public sealed record ProfileAvatarStyle(string Id, string Label, string Summary)
{
    public override string ToString() => Label;
}

/// <summary>A preset picture: its id (<c>dicebear:identicon:cosmic-otter</c> or <c>avatar-3</c>), name and preview.</summary>
public sealed record ProfileAvatarPreset(string Id, string Label, Uri PreviewUrl)
{
    public override string ToString() => Label;
}

/// <summary>
/// The preset pictures a profile can have, as the web's <c>lib/profile-avatars.ts</c> builds them: DiceBear styles with
/// eighteen seeds a batch, and the server's own <c>avatar-1</c> … <c>avatar-8</c> for a profile that already has one.
/// </summary>
public static class ProfileAvatarPresets
{
    /// <summary>The pictures a batch shows.</summary>
    public const int BatchSize = 18;

    private const string DiceBearPrefix = "dicebear:";
    private const string PresetPrefix = "preset:";

    private static readonly string[] Adjectives =
    [
        "cosmic", "jelly", "starlight", "mango", "bubble", "neon", "marble", "comet", "snappy", "pepper", "glimmer", "ripple",
        "pocket", "candy", "ember", "mochi", "sprout", "twinkle", "whiz", "plasma", "mint", "velvet", "crunchy", "sunbeam",
        "toffee", "splash", "boomer", "lunar", "fizzy", "biscuit", "rocket", "cobalt",
    ];

    private static readonly string[] Nouns =
    [
        "otter", "rocket", "fox", "cookie", "gecko", "bunny", "penguin", "puffin", "tiger", "panda", "saturn", "wizard",
        "skater", "nebula", "robot", "pirate", "meteor", "sprite", "pebble", "nova", "dragon", "donut", "parrot", "panther",
        "goblin", "muffin", "laser", "pickle", "falcon", "sunset", "toaster", "bandit",
    ];

    private static readonly (string Id, string Key)[] StyleKeys =
    [
        ("identicon", "identicon"),
        ("initials", "initials"),
        ("bottts-neutral", "botttsNeutral"),
        ("fun-emoji", "funEmoji"),
        ("pixel-art-neutral", "pixelArtNeutral"),
    ];

    private static readonly string[] LegacyKeys = ["skyFox", "cometCat", "starBear", "neonPup", "orbitBunny", "solarOwl", "novaGecko", "pixelPenguin"];

    public static IReadOnlyList<ProfileAvatarStyle> Styles() =>
    [
        .. StyleKeys.Select(style => new ProfileAvatarStyle(style.Id,
            AppText.Get($"web.profiles.helpers.avatar.style.{style.Key}.label"),
            AppText.Get($"web.profiles.helpers.avatar.style.{style.Key}.summary"))),
    ];

    /// <summary>One batch of a style's pictures; another batch shows other seeds (More options).</summary>
    public static IReadOnlyList<ProfileAvatarPreset> Batch(string styleId, int batch)
    {
        string style = StyleKeys.Any(s => s.Id == styleId) ? styleId : StyleKeys[0].Id;
        return
        [
            .. Enumerable.Range(0, BatchSize).Select(index => DiceBear(style,
                Adjectives[(batch * 7 + index * 3 + style.Length) % Adjectives.Length] + "-"
                + Nouns[(batch * 11 + index * 5 + style.Length * 2) % Nouns.Length])),
        ];
    }

    /// <summary>
    /// The preset a stored avatar names (<c>preset:dicebear:…</c>, <c>dicebear:…</c> or <c>avatar-3</c>), or null when it
    /// names none the editor knows, as the web's <c>resolveProfileAvatarPreset</c>.
    /// </summary>
    public static ProfileAvatarPreset? Resolve(string? avatar, Uri serverBase)
    {
        if (string.IsNullOrEmpty(avatar))
        {
            return null;
        }
        string id = avatar.StartsWith(PresetPrefix, StringComparison.Ordinal) ? avatar[PresetPrefix.Length..] : avatar;
        if (id.StartsWith(DiceBearPrefix, StringComparison.Ordinal))
        {
            string[] parts = id.Split(':');
            return parts.Length == 3 && StyleKeys.Any(s => s.Id == parts[1]) && IsSafeSeed(parts[2]) ? DiceBear(parts[1], parts[2]) : null;
        }
        if (id.StartsWith("avatar-", StringComparison.Ordinal) && int.TryParse(id["avatar-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n is >= 1 and <= 8 && id == $"avatar-{n}")
        {
            return new ProfileAvatarPreset(id, AppText.Get($"web.profiles.helpers.avatar.legacy.{LegacyKeys[n - 1]}"),
                new Uri(serverBase, $"profile-avatars/{id}.svg"));
        }
        return null;
    }

    /// <summary>The style of a DiceBear preset, else null.</summary>
    public static string? StyleOf(string? presetId) =>
        presetId is not null && presetId.StartsWith(DiceBearPrefix, StringComparison.Ordinal) ? presetId.Split(':')[1] : null;

    /// <summary>What a preset is sent as (<c>preset:&lt;id&gt;</c>).</summary>
    public static string Reference(string presetId) => PresetPrefix + presetId;

    // As the server takes a seed (profile_avatars.go, isSafeAvatarSeed).
    private static bool IsSafeSeed(string seed) => seed.Length is > 0 and <= 64 && seed.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static ProfileAvatarPreset DiceBear(string style, string seed) => new($"{DiceBearPrefix}{style}:{seed}",
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(seed.Replace('-', ' ')),
        new Uri($"https://api.dicebear.com/9.x/{style}/svg?seed={Uri.EscapeDataString(seed)}&size=128&radius=24&backgroundType=gradientLinear"));
}
