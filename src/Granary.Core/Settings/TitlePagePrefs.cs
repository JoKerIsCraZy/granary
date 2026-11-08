using System.Collections.Frozen;
using System.Text.Json;
using Granary.Core.Api.Settings;

namespace Granary.Core.Settings;

/// <summary>
/// What a title's page shows, as the profile chose in Settings → Title Pages (<see cref="TitlePageSettings"/>):
/// <see cref="Unknown"/> until the profile's choice is read.
/// </summary>
public sealed record TitlePagePrefs
{
    /// <summary>Where an effective value comes from when nothing is stored: the contract's default, or the administrator's.</summary>
    public const string DefaultSource = "default";

    /// <summary>Nothing read yet: every switch at its default, none offered, and the title art not known.</summary>
    public static readonly TitlePagePrefs Unknown = new();

    /// <summary>The contract's defaults, for a page without the settings and a profile whose read failed.</summary>
    public static readonly TitlePagePrefs Defaults = new() { ShowsTitleArt = true };

    /// <summary>The settings the server keeps for the profile; only those have a switch.</summary>
    public IReadOnlySet<string> Offered { get; init; } = FrozenSet<string>.Empty;

    /// <summary><c>catalog.show_advisory_age</c>: a title's page names its suggested minimum age. Off unless the profile turned it on.</summary>
    public bool ShowsAdvisoryAge { get; init; }

    /// <summary>
    /// <c>ui.title_art</c>: a title's page names it with its logo, when it has one, rather than its name in text. Null while
    /// the profile's choice is read, so the page holds the title instead of showing the wrong one, as the web's
    /// <c>useShowTitleArt</c>; on, the contract's default, on a server without the key.
    /// </summary>
    public bool? ShowsTitleArt { get; init; }

    /// <summary>Where the title art's value comes from: <c>profile</c> (every device), <c>profile_device</c> (this PC), or <see cref="DefaultSource"/>.</summary>
    public string TitleArtSource { get; init; } = DefaultSource;

    /// <summary>"Apply to all devices": the profile's value, which outranks every device's (the manifest's notes on <c>ui.title_art</c>).</summary>
    public bool TitleArtForAllDevices => TitleArtSource == SettingScopes.Profile;

    /// <summary>Neither this PC nor the profile chose: the server's default, which an administrator may set, applies.</summary>
    public bool TitleArtFollowsServerDefault => TitleArtSource == DefaultSource;

    /// <summary>The settings as the server answered them; a key it does not know is missing.</summary>
    internal static TitlePagePrefs From(IReadOnlyDictionary<string, EffectiveSetting> values)
    {
        var titleArt = values.GetValueOrDefault(TitlePageSettings.TitleArtKey);
        return new()
        {
            Offered = values.Keys.ToFrozenSet(StringComparer.Ordinal),
            ShowsAdvisoryAge = IsTrue(values, TitlePageSettings.AdvisoryAgeKey),
            // Anything but false is the contract's default, on.
            ShowsTitleArt = titleArt?.Value.ValueKind != JsonValueKind.False,
            TitleArtSource = titleArt?.Source is SettingScopes.Profile or SettingScopes.ProfileDevice ? titleArt.Source : DefaultSource,
        };
    }

    // Anything but true reads as off, as the web's switches read it.
    private static bool IsTrue(IReadOnlyDictionary<string, EffectiveSetting> values, string key) =>
        values.GetValueOrDefault(key)?.Value.ValueKind == JsonValueKind.True;
}
