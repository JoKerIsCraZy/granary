using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>What a party started from a title's page puts up first: a movie, or one episode (the web's <c>StartPartyTarget</c>).</summary>
/// <param name="ContentType"><c>movie</c> or <c>episode</c>.</param>
/// <param name="Subtitle">A movie's year, an episode's "S3 E1": data the room stores and sends as the web does, not worded.</param>
/// <param name="SeriesTitle">An episode's series, which its suggestion names.</param>
public sealed record StartPartyTarget(string ContentId, string ContentType, string Title, string Subtitle, string? SeriesTitle = null)
{
    /// <summary>The sheet's Episode row: <c>S3 E1 "Chapter 1"</c>.</summary>
    public string EpisodeText => Subtitle.Length > 0
        ? AppText.Get("web.watchTogether.startSheet.episodeWithCode", ("code", Subtitle), ("title", Title))
        : AppText.Get("web.watchTogether.startSheet.episodeTitle", ("title", Title));

    /// <summary>The room's name among the recent parties: "Chapter 1 · S3 E1".</summary>
    public string RoomTitle => WatchPartyText.Join(Title, Subtitle);

    /// <summary>A suggestion's subtitle, as the room's own suggestions word it: the year, or "Lupin · S3 E1".</summary>
    public string SuggestionSubtitle => SeriesTitle is { Length: > 0 } series ? WatchPartyText.Join(series, Subtitle) : Subtitle;

    /// <summary>An episode's "S3 E1".</summary>
    public static string EpisodeCode(int season, int episode) =>
        string.Create(CultureInfo.InvariantCulture, $"S{season} E{episode}");
}

/// <summary>A title's page as the start sheet shows it, and what the party would put up first.</summary>
public sealed record StartPartyRequest
{
    /// <summary>The page's title: the movie's, the series', or the episode's own.</summary>
    public required string Title { get; init; }

    /// <summary>The line under it: "Series · 3 seasons", a season's name, or the year.</summary>
    public string Caption { get; init; } = "";

    public Uri? Poster { get; init; }

    /// <summary>The poster's placeholder; "" for none (x:Bind skips a function whose argument is null).</summary>
    public string PosterThumbhash { get; init; } = "";

    /// <summary>The poster as the server named it, which a suggestion carries.</summary>
    public string? PosterPath { get; init; }

    /// <summary>The movie or episode the page would play; null for a series until its next episode is read.</summary>
    public StartPartyTarget? Target { get; init; }

    /// <summary>A series' next episode, to be named from its season's list (<see cref="SeasonNumber"/>).</summary>
    public string? NextUpId { get; init; }

    /// <summary>The series to pick another episode of; null for a movie.</summary>
    public string? SeriesId { get; init; }

    public string? SeriesTitle { get; init; }

    /// <summary>The season the episode chooser opens on.</summary>
    public int? SeasonNumber { get; init; }
}

/// <summary>A season pill of the episode chooser: "Season 3" or "Specials".</summary>
public sealed partial class StartPartySeason(int number, string label) : ObservableObject
{
    public int Number { get; } = number;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>An episode the chooser offers: one with a file to play.</summary>
public sealed record StartPartyEpisode(string ContentId, int SeasonNumber, int EpisodeNumber, string Title, bool IsWatched)
{
    /// <summary>"E1".</summary>
    public string Number => AppText.Get("web.watchTogether.common.episodeNumber", ("number", EpisodeNumber.ToString(CultureInfo.InvariantCulture)));

    /// <summary>"Episode 1, Chapter 1, watched".</summary>
    public string AccessibleName => AppText.Get("granary.watchParty.episodeName", ("number", EpisodeNumber.ToString(CultureInfo.InvariantCulture)),
        ("title", Title), ("watched", IsWatched ? "yes" : "no"));
}

/// <summary>
/// The room page opened by a party just started from a title's page, with what the web would toast on the way: the
/// invite copied, or what did not go through.
/// </summary>
public sealed record WatchPartyArrival(string Notice);
