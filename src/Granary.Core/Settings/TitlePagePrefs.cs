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
    /// <summary>Nothing read yet: every switch at its default, none offered.</summary>
    public static readonly TitlePagePrefs Unknown = new();

    /// <summary>The settings the server keeps for the profile; only those have a switch.</summary>
    public IReadOnlySet<string> Offered { get; init; } = FrozenSet<string>.Empty;

    /// <summary><c>catalog.show_advisory_age</c>: a title's page names its suggested minimum age. Off unless the profile turned it on.</summary>
    public bool ShowsAdvisoryAge { get; init; }

    /// <summary>The settings as the server answered them; a key it does not know is missing.</summary>
    internal static TitlePagePrefs From(IReadOnlyDictionary<string, EffectiveSetting> values) => new()
    {
        Offered = values.Keys.ToFrozenSet(StringComparer.Ordinal),
        ShowsAdvisoryAge = IsTrue(values, TitlePageSettings.AdvisoryAgeKey),
    };

    // Anything but true reads as off, as the web's switches read it.
    private static bool IsTrue(IReadOnlyDictionary<string, EffectiveSetting> values, string key) =>
        values.GetValueOrDefault(key)?.Value.ValueKind == JsonValueKind.True;
}
