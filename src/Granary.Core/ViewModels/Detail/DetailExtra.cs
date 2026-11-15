using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// A local extra on a movie's or series' page (the web's <c>ExtrasSection.tsx</c>): a featurette, deleted scene or the like
/// among the server's files, played in Granary's player by its content id.
/// </summary>
/// <param name="Title">Its own title; without one its group's label ("Featurettes"), as the web's card names it.</param>
/// <param name="Duration">"12:22" or "1:02:09", as the web's <c>formatClock</c>; empty when the server does not know it.</param>
/// <param name="StillUrl">Its 16:9 picture; presigned and short-lived, so it may fail to load.</param>
/// <param name="StillThumbhash">The picture's placeholder, shown until the picture is in, and in its place when it fails.</param>
/// <param name="AccessibleName">"Making Of, Featurette, 12:22, plays the extra".</param>
public sealed record DetailExtra(string ContentId, string Title, string Duration, Uri? StillUrl, string? StillThumbhash, string AccessibleName)
{
    /// <summary>The extra as its card shows it; null for one without a content id, which nothing could play.</summary>
    public static DetailExtra? From(ItemExtra extra, Uri server)
    {
        ArgumentNullException.ThrowIfNull(extra);
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(extra.ContentId))
        {
            return null;
        }
        bool titled = !string.IsNullOrWhiteSpace(extra.Title);
        string title = titled ? extra.Title!.Trim() : ExtraKinds.GroupLabel(extra.Kind);
        string duration = extra.DurationSeconds is long seconds and > 0 ? DetailFacts.Clock(seconds) : "";
        // The kind is said once: a card named by its group says it already.
        string?[] spoken = [title, titled ? ExtraKinds.Label(extra.Kind) : null, duration];
        return new DetailExtra(
            extra.ContentId,
            title,
            duration,
            ArtworkUrl.Resolve(server, extra.StillUrl),
            string.IsNullOrWhiteSpace(extra.StillThumbhash) ? null : extra.StillThumbhash,
            AppText.Get("granary.detail.extraName", ("extra", string.Join(", ", spoken.Where(part => !string.IsNullOrEmpty(part))))));
    }
}

/// <summary>The extras of one kind under their heading, as the web's <c>ExtrasSection.tsx</c> shows them.</summary>
/// <param name="Label">The web's group label, "Deleted Scenes"; screen readers hear the heading in these words.</param>
public sealed record DetailExtraGroup(string Label, IReadOnlyList<DetailExtra> Extras)
{
    /// <summary>The heading as shown, in capitals as the web's CSS shows it.</summary>
    public string Heading => AppText.Upper(Label);

    /// <summary>
    /// The extras by kind: the groups in the order their kinds first come, each extra in the server's order (the server
    /// sorts them by kind already). Extras nothing could play are left out.
    /// </summary>
    /// <remarks>
    /// Unlike the web, which groups by the kind exactly as the server spells it, a group here is the kind's name in the
    /// server's list (<see cref="ExtraKinds.Key"/>): other spellings of a kind share its group, and every kind the list does
    /// not have goes into one "Other" group, where the web would show an "Other" heading for each. The server folds unknown
    /// kinds to "other" itself, so both show the same for what it sends today.
    /// </remarks>
    public static IReadOnlyList<DetailExtraGroup> From(IEnumerable<ItemExtra> extras, Uri server)
    {
        ArgumentNullException.ThrowIfNull(extras);
        ArgumentNullException.ThrowIfNull(server);
        return [.. extras.OfType<ItemExtra>()
            .Select(extra => (Kind: ExtraKinds.Key(extra.Kind), Card: DetailExtra.From(extra, server)))
            .Where(extra => extra.Card is not null)
            .GroupBy(extra => extra.Kind, StringComparer.Ordinal)
            .Select(group => new DetailExtraGroup(ExtraKinds.GroupLabel(group.Key), [.. group.Select(extra => extra.Card!)]))];
    }
}
