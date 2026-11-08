using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// What a season's grid places for the episodes its listing lacks (ItemDetailViewModel.Airings.cs): the gaps between the
/// episodes it lists, the calendar windows that can name them and those after the last, and the places made of the answers.
/// </summary>
internal static class SeasonGaps
{
    /// <summary>The calendar requests a season page sends at most.</summary>
    public const int MaxWindows = 4;

    /// <summary>
    /// The widest gap that shows the numbers the calendar does not name too. A wider one is more likely numbering (1 to 12,
    /// then 101) or a stray number than missing episodes, so it shows only those the calendar names.
    /// </summary>
    public const int MaxUnnamedGap = 10;

    /// <summary>The places a season shows at most, the lowest numbers first.</summary>
    public const int MaxPlaces = 50;

    // How many episodes past its first one a multi-episode file can hold, as the server's MaxEpisodeSpan.
    private const int MaxEpisodeSpan = 10;

    /// <summary>The numbers missing between two listed episodes, <paramref name="First"/> to <paramref name="Last"/>.</summary>
    public sealed record Gap(int First, int Last, Episode Before, Episode After)
    {
        public int Width => Last - First + 1;
    }

    /// <summary>Local days, both included, at most <see cref="CalendarApi.MaxDays"/> apart: one calendar request.</summary>
    public readonly record struct Window(DateOnly Start, DateOnly End)
    {
        public int Span => End.DayNumber - Start.DayNumber;
    }

    /// <summary>
    /// A place, and the listed episode whose multi-episode file might hold it, which is read before it shows; none for an
    /// episode still to air, or one of a season the listing has nothing of.
    /// </summary>
    public sealed record Planned(DetailEpisodePlaceholder Place, Episode? HeldBy);

    public static IReadOnlyList<Gap> Find(IReadOnlyList<Episode> listed)
    {
        var numbered = listed.Where(e => e.EpisodeNumber > 0).GroupBy(e => e.EpisodeNumber).OrderBy(g => g.Key).Select(g => g.First()).ToList();
        List<Gap> gaps = [];
        for (int i = 1; i < numbered.Count; i++)
        {
            var (before, after) = (numbered[i - 1], numbered[i]);
            if (after.EpisodeNumber - before.EpisodeNumber > 1)
            {
                gaps.Add(new Gap(before.EpisodeNumber + 1, after.EpisodeNumber - 1, before, after));
            }
        }
        return gaps;
    }

    /// <summary>
    /// The windows to ask, at most <see cref="MaxWindows"/>: today's 62 days in two, always; then, the latest first, each
    /// gap's, from one neighbour's air date to the other's when they are at most 31 days apart, and the 31 days after the
    /// last episode's up to yesterday. Windows that fit in one are asked as one.
    /// </summary>
    public static IReadOnlyList<Window> Windows(IReadOnlyList<Gap> gaps, Episode? last, DateOnly today)
    {
        List<Window> wanted = [];
        foreach (var gap in gaps)
        {
            if (DateOf(gap.Before.AirDate) is { } before && DateOf(gap.After.AirDate) is { } after)
            {
                var window = before <= after ? new Window(before, after) : new Window(after, before);
                // A day more at each end where it fits: the calendar lists by the viewer's day, which can be a day off the
                // source's air date that the listing has.
                var wider = new Window(window.Start.AddDays(-1), window.End.AddDays(1));
                if (wider.Span <= CalendarApi.MaxDays)
                {
                    wanted.Add(wider);
                }
                else if (window.Span <= CalendarApi.MaxDays)
                {
                    wanted.Add(window);
                }
            }
        }
        // Today itself is in today's first window.
        var yesterday = today.AddDays(-1);
        if (DateOf(last?.AirDate) is { } aired && aired <= yesterday)
        {
            var end = aired.AddDays(CalendarApi.MaxDays);
            wanted.Add(new Window(aired, end < yesterday ? end : yesterday));
        }
        Window[] todays = [new(today, today.AddDays(CalendarApi.MaxDays)), new(today.AddDays(CalendarApi.MaxDays + 1), today.AddDays(2 * CalendarApi.MaxDays + 1))];

        // Today's windows span the most a request may, so they only take in windows they hold and stay as they are.
        var merged = Merge([.. wanted, .. todays]);
        return [.. merged.Where(todays.Contains)
            .Concat(merged.Where(w => !todays.Contains(w)).OrderByDescending(w => w.Start).Take(MaxWindows - todays.Length))
            .OrderBy(w => w.Start)];
    }

    /// <summary>The numbers the episode's multi-episode files hold, its own included.</summary>
    public static IReadOnlyCollection<int> Spanned(CatalogItemDetail episode) => episode.Versions
        .Where(v => v.MultiEpisodeStart is { } start && v.MultiEpisodeEnd is { } end && end > start)
        .SelectMany(v => Enumerable.Range(v.MultiEpisodeStart!.Value, Math.Min(v.MultiEpisodeEnd!.Value - v.MultiEpisodeStart.Value, MaxEpisodeSpan) + 1))
        .ToHashSet();

    /// <summary>
    /// The places by number, at most <see cref="MaxPlaces"/>: a gap's numbers as the calendar names them, and in a narrow gap
    /// those it does not name too; then the calendar's episodes after the last listed one. An episode aired is missing, one
    /// still to air upcoming.
    /// </summary>
    /// <param name="today">The day in the zone the calendar was asked for.</param>
    /// <param name="zone">The viewer's zone, which an airing's instant is shown in.</param>
    public static IReadOnlyList<Planned> Places(int season, IReadOnlyList<Gap> gaps, Episode? last, IEnumerable<EpisodeAiring> airings,
        DateTimeOffset now, DateOnly today, TimeZoneInfo zone)
    {
        var named = airings.Where(a => a.SeasonNumber == season).GroupBy(a => a.EpisodeNumber).ToDictionary(g => g.Key, g => g.First());
        List<Planned> places = [];
        foreach (var gap in gaps)
        {
            var numbers = gap.Width <= MaxUnnamedGap
                ? Enumerable.Range(gap.First, gap.Width)
                : named.Keys.Where(n => n >= gap.First && n <= gap.Last);
            foreach (int number in numbers)
            {
                places.Add(named.TryGetValue(number, out var airing) ? Of(season, number, airing, gap.Before, now, today, zone) : new(Missing(number), gap.Before));
            }
        }
        int lastListed = last?.EpisodeNumber ?? 0;
        foreach (var (number, airing) in named.Where(pair => pair.Key > lastListed))
        {
            places.Add(Of(season, number, airing, last, now, today, zone));
        }
        return [.. places.OrderBy(p => p.Place.EpisodeNumber).Take(MaxPlaces)];
    }

    /// <summary>A season listing that has two episodes with one file lists a double episode's second half: no gap hides one.</summary>
    public static bool ListsBothHalves(IReadOnlyList<Episode> listed) => listed
        .SelectMany(e => e.Files.Select(f => f.FileId)).Where(id => id.Length > 0)
        .GroupBy(id => id, StringComparer.Ordinal).Any(file => file.Count() > 1);

    // Still to air, it is upcoming and no file can hold it; else missing, unless the file of heldBy holds it.
    private static Planned Of(int season, int number, EpisodeAiring airing, Episode? heldBy, DateTimeOffset now, DateOnly today, TimeZoneInfo zone)
    {
        var day = Day(airing, zone);
        if (!HasAired(airing, now, today) && day is { } airs)
        {
            return new(new DetailEpisodePlaceholder(number, IsMissing: false, Code(season, number, airing), $"Airs {CalendarText.ShortDate(airs)}",
                $"Episode {number}, airs {CalendarText.MonthDay(airs)}"), null);
        }
        string? date = day is { } aired ? MediaCardFactory.FormatDate(aired) : null;
        return new(new DetailEpisodePlaceholder(number, IsMissing: true, Code(season, number, airing), date, $"Episode {number}, missing"), heldBy);
    }

    // By its instant when the calendar knows it, else by the day it is listed under; one without either counts as aired.
    private static bool HasAired(EpisodeAiring airing, DateTimeOffset now, DateOnly today) => airing.AirAt is { } at
        ? at <= now
        : (DateOf(airing.LocalAirDate) ?? DateOf(airing.AirDate)) is not { } day || day < today;

    private static DetailEpisodePlaceholder Missing(int number) => new(number, IsMissing: true, $"Episode {number}", null, $"Episode {number}, missing");

    // As an episode card's title: "S01E05 · Title".
    private static string Code(int season, int number, EpisodeAiring airing) =>
        airing.Title?.Trim() is { Length: > 0 } title ? $"S{season:00}E{number:00} · {title}" : $"S{season:00}E{number:00}";

    // The viewer's day: the instant's in the viewer's zone, else the day the calendar listed it under, else the source's.
    private static DateOnly? Day(EpisodeAiring airing, TimeZoneInfo zone) => airing.AirAt is { } at
        ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime)
        : DateOf(airing.LocalAirDate) ?? DateOf(airing.AirDate);

    private static DateOnly? DateOf(string? isoDate) =>
        isoDate is { Length: >= 10 } && DateOnly.TryParseExact(isoDate[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    // Windows that fit in one together are asked as one.
    private static List<Window> Merge(IEnumerable<Window> windows)
    {
        List<Window> merged = [];
        foreach (var window in windows.OrderBy(w => w.Start).ThenByDescending(w => w.End))
        {
            if (merged.Count > 0)
            {
                var open = merged[^1];
                var joined = new Window(open.Start, open.End > window.End ? open.End : window.End);
                if (joined.Span <= CalendarApi.MaxDays)
                {
                    merged[^1] = joined;
                    continue;
                }
            }
            merged.Add(window);
        }
        return merged;
    }
}
