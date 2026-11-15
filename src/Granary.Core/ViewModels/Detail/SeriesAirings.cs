using Granary.Core.Api.Catalog;
using Granary.Core.Auth;

namespace Granary.Core.ViewModels.Detail;

/// <summary>An episode's airing as the calendar lists it, with only what a season's grid and the hero's next airing read.</summary>
/// <param name="AirDate">The source's air date, YYYY-MM-DD.</param>
/// <param name="LocalAirDate">The viewer's day it is listed under, YYYY-MM-DD.</param>
/// <param name="IsSeasonPremiere">
/// The calendar lists the season by itself, its first episode having no air date: it stands for episode 1, as Apple's
/// <c>AutoDownloadSchedule</c> reads it. A season's grid places no episode by it.
/// </param>
public sealed record EpisodeAiring(int SeasonNumber, int EpisodeNumber, string? Title, string? AirDate, string? LocalAirDate, DateTimeOffset? AirAt,
    bool IsSeasonPremiere = false);

/// <summary>
/// The episode airings from the calendar (docs/api-contract.md 7.13) that a season's grid and an episode's More Episodes
/// place the missing and upcoming episodes by, and that the hero names the series' next airing by. A window's answer lists
/// every series, so it is asked once per server, profile, zone and window, by
/// every page that wants it at the same time, and kept for a few minutes by series: the seasons of every series share
/// today's windows. A change of server, profile or session forgets them all. One for the whole app.
/// </summary>
public sealed class SeriesAirings
{
    /// <summary>How long an answer is used again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);

    /// <summary>How long a request may take before every page waiting for it is told it failed.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    // A season the calendar lists by itself, its first episode having no air date.
    private const string SeasonPremiere = "season_premiere";

    private readonly CalendarApi _calendar;
    private readonly KeptAnswers<Key, IReadOnlyDictionary<string, IReadOnlyList<EpisodeAiring>>> _windows;

    public SeriesAirings(CalendarApi calendar, ISessionManager session, TimeProvider time)
    {
        _calendar = calendar;
        // A request is sent as whichever profile acts when it goes out: an answer that came while another acts may be theirs.
        _windows = new(time, KeepFor, GiveUpAfter, key => Acts(session, key.Server, key.Profile));
        session.Changed += (_, _) => _windows.Clear();
    }

    /// <summary>
    /// The series' episodes airing from <paramref name="start"/> to <paramref name="end"/>, local days in
    /// <paramref name="timeZone"/>, at most <see cref="CalendarApi.MaxDays"/> apart.
    /// </summary>
    /// <param name="profileId">The profile the calendar is read for: what it may see decides what is listed.</param>
    public async Task<IReadOnlyList<EpisodeAiring>> GetAsync(Uri server, string? profileId, string seriesId, DateOnly start, DateOnly end,
        string? timeZone, CancellationToken cancellationToken)
    {
        var bySeries = await _windows.GetAsync(new Key(server.AbsoluteUri, profileId, timeZone, start, end),
            giveUp => FetchAsync(server, start, end, timeZone, giveUp), cancellationToken);
        return bySeries.GetValueOrDefault(seriesId) ?? [];
    }

    /// <summary>The session still acts on <paramref name="server"/> as <paramref name="profileId"/>.</summary>
    internal static bool Acts(ISessionManager session, string server, string? profileId) =>
        session.ActiveServer?.BaseUri.AbsoluteUri == server && session.ProfileId == profileId;

    // Given up only by its own time limit, not by the page that asked first: others may be waiting for the same window.
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<EpisodeAiring>>> FetchAsync(Uri server, DateOnly start, DateOnly end, string? timeZone,
        CancellationToken giveUp)
    {
        var answer = await _calendar.GetAsync(server, start, end, timeZone: timeZone, cancellationToken: giveUp);
        return answer.Events.SelectMany(day => day.Items)
            .Where(a => !string.IsNullOrEmpty(a.SeriesId) && a.SeasonNumber is > 0
                && (a.Type == SeasonPremiere || (a.Type == "episode" && a.EpisodeNumber is > 0)))
            .GroupBy(a => a.SeriesId!, StringComparer.Ordinal)
            .ToDictionary(series => series.Key, series => (IReadOnlyList<EpisodeAiring>)[.. series.Select(a =>
                new EpisodeAiring(a.SeasonNumber!.Value, a.EpisodeNumber is > 0 and int number ? number : 1, a.EpisodeTitle, a.AirDate,
                    a.LocalAirDate, a.AirAt, a.Type == SeasonPremiere))],
                StringComparer.Ordinal);
    }

    private readonly record struct Key(string Server, string? Profile, string? TimeZone, DateOnly Start, DateOnly End);
}
