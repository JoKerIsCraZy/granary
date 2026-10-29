using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Detail;

/// <summary>A metadata chip under the title: "2026", "R", "1H 49M"; accent chips carry the technical facts ("1080P", "EAC3").</summary>
public sealed record DetailChip(string Text, bool IsAccent = false);

/// <summary>An external rating as the server words it: "IMDb" and "8.7".</summary>
public sealed record DetailRating(string Name, string Display)
{
    public string AccessibleName => $"{Name} {Display}";
}

/// <summary>A cast or crew card: photo (initials without one), name and role or character.</summary>
/// <param name="PersonId">The catalog person the card opens; null for one known only from TMDB.</param>
public sealed record DetailPerson(string Name, string? Role, Uri? PhotoUrl, string? PersonId, bool IsCrew = false)
{
    public string Initials => ProfileItemViewModel.InitialsOf(Name);

    /// <summary>The card leads to the person's page.</summary>
    public bool CanOpen => !string.IsNullOrWhiteSpace(PersonId);
}

/// <summary>A crumb above a season or episode title; <see cref="TargetId"/> is the item it opens, if any.</summary>
public sealed record DetailCrumb(string Label, string? TargetId, bool IsFirst)
{
    public bool IsLink => TargetId is not null;
}

/// <summary>An episode in a season grid: the shared card plus up to three lines of its overview.</summary>
public sealed record DetailEpisode(MediaCard Card, string? Overview)
{
    public string Id => Card.Id;

    public string AccessibleName => Card.IsWatched ? $"{Card.AccessibleName}, watched" : Card.AccessibleName;
}

/// <summary>A part of the Info dialog: "Details", "Cast", "Crew", "File".</summary>
public sealed record DetailInfoSection(string Title, IReadOnlyList<DetailInfoRow> Rows);

/// <summary>A label and its value in the Info dialog: "Runtime" and "1h 58m", or an actor and their role.</summary>
public sealed record DetailInfoRow(string Label, string Value);

/// <summary>What the primary button says and which item it would play.</summary>
public sealed record DetailPlayAction(string Label, string? TargetId);
