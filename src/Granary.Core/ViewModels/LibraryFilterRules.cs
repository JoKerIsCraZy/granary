using System.Text.Json;
using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels;

/// <summary>
/// The rule groups of the filters the simple listing cannot take, as Silo's Apple and Android apps build them
/// (<c>CatalogQueryBuilder.swift</c>): a group per facet, any of its values matching; the video qualities and the watch
/// status each a group that must all match. The groups combine with all. Only fields and conditions the server's rule
/// allowlist takes (docs/api-contract.md, 7.4); years and ratings stay the listing's simple filters.
/// </summary>
public static class LibraryFilterRules
{
    public static IReadOnlyList<SmartRuleGroup> For(LibraryFilterSet filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var groups = new List<SmartRuleGroup>();
        AddAny(groups, filters.Genres.Select(g => Rule("genre", "contains", SmartValue.Text(g))));
        AddAny(groups, filters.Decades.Select(d => Rule("year", "between", SmartValue.Range(Year(d), Year(d + 9)))));
        AddAll(groups, VideoQuality(filters));
        AddAny(groups, filters.OriginalLanguages.Select(l => Rule("original_language", "is", SmartValue.Text(l))));
        AddAny(groups, filters.AudioLanguages.Select(l => Rule("audio_language", "is", SmartValue.Text(l))));
        AddAny(groups, filters.SubtitleLanguages.Select(l => Rule("subtitle_language", "is", SmartValue.Text(l))));
        AddAll(groups, WatchStatus(filters.WatchStatus));
        return groups;
    }

    private static IEnumerable<SmartRule> VideoQuality(LibraryFilterSet filters)
    {
        if (filters.FourK)
        {
            yield return Rule("resolution", "is", SmartValue.Text("2160p"));
        }
        if (filters.Hdr)
        {
            yield return Rule("hdr", "is", SmartValue.Bool(true));
        }
        if (filters.DolbyVision)
        {
            yield return Rule("dolby_vision", "is", SmartValue.Bool(true));
        }
    }

    // Unwatched is neither watched nor started, as the web's guided filters have it.
    private static IEnumerable<SmartRule> WatchStatus(LibraryWatchStatus status) => status switch
    {
        LibraryWatchStatus.Watched => [Rule("watched", "is", SmartValue.Bool(true))],
        LibraryWatchStatus.InProgress => [Rule("in_progress", "is", SmartValue.Bool(true))],
        LibraryWatchStatus.Unwatched => [Rule("watched", "is", SmartValue.Bool(false)), Rule("in_progress", "is", SmartValue.Bool(false))],
        _ => [],
    };

    private static void AddAny(List<SmartRuleGroup> groups, IEnumerable<SmartRule> rules) => Add(groups, SmartMatch.Any, rules);

    private static void AddAll(List<SmartRuleGroup> groups, IEnumerable<SmartRule> rules) => Add(groups, SmartMatch.All, rules);

    private static void Add(List<SmartRuleGroup> groups, string match, IEnumerable<SmartRule> rules)
    {
        SmartRule[] given = [.. rules];
        if (given.Length > 0)
        {
            groups.Add(new SmartRuleGroup(match, given));
        }
    }

    private static SmartRule Rule(string field, string op, JsonElement value) => new() { Field = field, Op = op, Value = value };

    private static JsonElement Year(int year) => SmartValue.Number(year);
}
