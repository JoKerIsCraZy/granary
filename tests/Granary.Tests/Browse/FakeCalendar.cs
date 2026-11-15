using System.Globalization;
using System.Net;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>
/// The server's calendar (docs/api-contract.md 7.13): each window answered with the airings listed under its days, as the
/// server trims them to the window it was asked for by the viewer's day.
/// </summary>
internal sealed class FakeCalendar
{
    public const string Path = "api/v2/calendar";

    private readonly List<Airing> _airings = [];

    /// <summary>Answers with 503 instead.</summary>
    public bool Fails { get; set; }

    /// <summary>Held until the test sets it, when set.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <param name="local">The viewer's day it is listed under, when it is not <paramref name="date"/>.</param>
    public FakeCalendar Airs(int episode, string title, string date, string? at = null, int season = 3, string series = ItemDetailPayloads.SeriesId,
        string? local = null)
    {
        _airings.Add(new Airing(series, season, episode, title, date, local ?? date, at, "episode"));
        return this;
    }

    public async Task<HttpResponseMessage> RespondAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
        if (Fails)
        {
            return await ScriptedSilo.Problem(503, "dependency_unavailable")();
        }
        var start = DateOnly.ParseExact(Query(uri, "start"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(Query(uri, "end"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = _airings.Where(a => a.Day >= start && a.Day <= end).GroupBy(a => a.Local).OrderBy(day => day.Key, StringComparer.Ordinal)
            .Select(day => $$"""{ "date": "{{day.Key}}", "items": [{{string.Join(",", day.Select(a => a.Json))}}] }""");
        return await ScriptedSilo.Json(HttpStatusCode.OK, $$"""{ "events": [{{string.Join(",", days)}}] }""")();
    }

    public static string Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).Single(pair => pair[0] == name)[1];

    private sealed record Airing(string Series, int Season, int? Episode, string Title, string Date, string Local, string? At, string Type)
    {
        public DateOnly Day => DateOnly.ParseExact(Local, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        public string Json => $$"""
            {
              "content_id": "{{Type}}-{{Series}}-{{Season}}-{{Episode}}", "type": "{{Type}}", "title": "Lupin", "series_id": "{{Series}}",
              "season_number": {{Season}}{{(Episode is { } number ? $", \"episode_number\": {number}" : "")}}, "episode_title": "{{Title}}",
              "air_date": "{{Date}}", "local_air_date": "{{Local}}"{{(At is null ? "" : $", \"air_at\": \"{At}\"")}},
              "badges": [{{(Type == "season_premiere" ? "\"season_premiere\"" : "")}}], "watched": false
            }
            """;
    }
}
