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
        string? libraryId = null, CancellationToken cancellationToken = default)
    {
        if (end < start || end.DayNumber - start.DayNumber > MaxDays)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, $"The window must end on or after its start and within {MaxDays} days.");
        }
        string query = new QueryBuilder()
            .Add("start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Add("end", end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Add("filter", filter)
            .Add("timezone", timeZone)
            .Add("library_id", libraryId)
            .ToString();
        return http.GetAsync(server, "api/v2/calendar" + query, SiloAuth.Profile, SiloJsonContext.Default.CalendarWeek, cancellationToken);
    }
}
