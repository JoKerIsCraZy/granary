using System.Globalization;

namespace Granary.Core.Api.Catalog;

/// <summary>The Calendar page's airings, for the selected profile.</summary>
public sealed class CalendarApi(SiloHttp http)
{
    /// <summary>The longest window the server answers, in days after <c>start</c>.</summary>
    public const int MaxDays = 30;

    /// <summary>The airings from <paramref name="start"/> to <paramref name="end"/>, both local days in <paramref name="timeZone"/>.</summary>
    /// <param name="filter">One of <see cref="CalendarFilters"/>; null is every airing.</param>
    /// <param name="timeZone">An IANA zone the days are reckoned in; null is UTC.</param>
    /// <param name="libraryId">Only this library's; null is every library.</param>
    public Task<CalendarWeek> GetAsync(Uri server, DateOnly start, DateOnly end, string? filter = null, string? timeZone = null,
        string? libraryId = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, PathOf(start, end, filter, timeZone, libraryId), SiloAuth.Profile, SiloJsonContext.Default.CalendarWeek, cancellationToken);

    /// <summary>
    /// As <see cref="GetAsync"/>, every library's, for a read that belongs to profile <paramref name="profileId"/> of account
    /// <paramref name="accountId"/>, as a monitored series' schedule: it goes out only while the server still acts as both, else
    /// it throws <see cref="ProfileChangedException"/> unsent.
    /// </summary>
    public Task<CalendarWeek> GetAsAccountProfileAsync(Uri server, string accountId, string profileId, DateOnly start, DateOnly end, string? filter,
        string? timeZone, CancellationToken cancellationToken) =>
        http.GetAsAccountProfileAsync(server, PathOf(start, end, filter, timeZone, null), accountId, profileId, SiloJsonContext.Default.CalendarWeek,
            cancellationToken);

    private static string PathOf(DateOnly start, DateOnly end, string? filter, string? timeZone, string? libraryId)
    {
        if (end < start || end.DayNumber - start.DayNumber > MaxDays)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, $"The window must end on or after its start and within {MaxDays} days.");
        }
        return "api/v2/calendar" + new QueryBuilder()
            .Add("start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Add("end", end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Add("filter", filter)
            .Add("timezone", timeZone)
            .Add("library_id", libraryId)
            .ToString();
    }
}
