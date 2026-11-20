using Granary.Core.Api.Profiles;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Profiles;

/// <summary>A choice of a profile's limits: the value sent and the text shown.</summary>
public sealed record ProfileChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A profile's playback ceiling as the web offers it (<c>lib/playback-quality.ts</c>).</summary>
public enum PlaybackCeiling
{
    Any,
    Standard,
    FourK,
}

/// <summary>
/// The limits a profile can be given and how the web says them (<c>lib/profile-management.ts</c>,
/// <c>lib/playback-quality.ts</c>): the content-rating ceilings, the advisory ages, the playback ceilings, and the line under
/// a profile in Settings → Profiles.
/// </summary>
public static class ProfileAccess
{
    /// <summary>A resolution's name, the same in every language.</summary>
    public const string FourK = "4K";

    /// <summary>The advisory-age limits the server takes.</summary>
    public const int MinAdvisoryAge = 1;

    public const int MaxAdvisoryAge = 21;

    /// <summary>
    /// The ceilings ordered by the youngest age each admits; the server enforces them as ages, so "12" limits a library
    /// certified in any national system. The labels list certificate codes, the same in every language.
    /// </summary>
    public static IReadOnlyList<ProfileChoice<string>> ContentRatings() =>
    [
        new("", AppText.Get("web.profiles.helpers.contentRating.any")),
        new("G", "G / TV-G / U"),
        new("6", "6 / FSK 6"),
        new("PG", "PG / TV-PG / TV-Y7"),
        new("12", "12 / 12A / FSK 12"),
        new("PG-13", "PG-13 / TV-14"),
        new("15", "15 / MA 15+"),
        new("16", "16 / FSK 16"),
        new("R", "R / TV-MA / NC-17 / 18"),
    ];

    /// <summary>No limit, then every limit the server takes, so a profile set elsewhere opens on its own.</summary>
    public static IReadOnlyList<ProfileChoice<int?>> AdvisoryAges() =>
    [
        new(null, AppText.Get("web.profiles.helpers.advisoryAge.noLimit")),
        .. Enumerable.Range(MinAdvisoryAge, MaxAdvisoryAge - MinAdvisoryAge + 1).Select(age => new ProfileChoice<int?>(age,
            AppText.Get("web.profiles.helpers.advisoryAge.agesAndUnder", ("age", age), ("age_text", AppText.Number(age))))),
    ];

    public static IReadOnlyList<ProfileChoice<PlaybackCeiling>> PlaybackCeilings() =>
    [
        new(PlaybackCeiling.Any, AppText.Get("web.profiles.helpers.quality.any")),
        new(PlaybackCeiling.Standard, AppText.Get("web.profiles.helpers.quality.standard")),
        new(PlaybackCeiling.FourK, FourK),
    ];

    /// <summary>A stored ceiling as the web reads it (<c>canonicalPlaybackQuality</c>): older values map onto the two.</summary>
    public static PlaybackCeiling CeilingOf(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "standard" or "480p" or "720p" or "1080p" => PlaybackCeiling.Standard,
        "4k" or "uhd" or "2160p" or "4320p" => PlaybackCeiling.FourK,
        _ => PlaybackCeiling.Any,
    };

    /// <summary>What a ceiling is sent as; null for none.</summary>
    public static string? WireValue(PlaybackCeiling ceiling) => ceiling switch
    {
        PlaybackCeiling.Standard => "1080p",
        PlaybackCeiling.FourK => "2160p",
        _ => null,
    };

    /// <summary>
    /// "PG max · All libraries · Any quality", the line under a profile, as the web's <c>buildProfileAccessSummary</c>: its
    /// content rating, advisory age, libraries, playback ceiling and, when blocked, requests.
    /// </summary>
    public static string Summary(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string rating = string.IsNullOrEmpty(profile.MaxContentRating) || !ContentRatings().Any(choice => choice.Value == profile.MaxContentRating)
            ? AppText.Get("web.profiles.helpers.contentRating.any")
            : AppText.Get("web.profiles.helpers.contentRating.max", ("rating", profile.MaxContentRating));
        string advisory = profile.MaxAdvisoryAge is { } age and > 0
            ? AppText.Get(profile.RequireAdvisoryAge ? "web.profiles.helpers.access.advisoryAgeMaxRatedOnly" : "web.profiles.helpers.access.advisoryAgeMax",
                ("age", age))
            : "";
        int libraryCount = LibraryIds(profile.AllowedLibraryIds).Count;
        string libraries = profile.LibraryRestrictionsEnabled
            ? AppText.Get("web.profiles.helpers.access.libraries", ("count", libraryCount), ("count_text", AppText.Number(libraryCount)))
            : AppText.Get("web.profiles.helpers.access.allLibraries");
        var ceiling = CeilingOf(profile.MaxPlaybackQuality);
        string quality = ceiling == PlaybackCeiling.Any
            ? AppText.Get("web.profiles.helpers.access.anyQuality")
            : AppText.Get("web.profiles.helpers.access.quality", ("quality", ceiling == PlaybackCeiling.FourK ? FourK : AppText.Get("web.profiles.helpers.quality.standard")));
        string requests = profile.MayRequest ? "" : AppText.Get("web.profiles.helpers.access.noRequests");
        return string.Join(" · ", new[] { rating, advisory, libraries, quality, requests }.Where(part => part.Length > 0));
    }

    /// <summary>Library ids as the web keeps them: positive numbers, each once, in ascending order.</summary>
    public static IReadOnlyList<string> LibraryIds(IEnumerable<string>? ids) =>
    [
        .. (ids ?? []).Select(id => long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n) ? n : 0)
            .Where(n => n > 0).Distinct().Order().Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];
}
