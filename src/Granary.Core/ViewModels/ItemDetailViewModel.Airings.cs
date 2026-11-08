using System.Collections.ObjectModel;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// The places of the episodes a season's grid lacks, Granary's own (docs/ui-design.md, "Detail pages"): one missing between
// those the library has or aired after its last, and one still to air. Silo's season listing leaves out an episode without
// a file; the calendar still names it (docs/api-contract.md 7.13). The places come after the load, which does not wait for
// them, and take no part in playing, marking, spoilers or More Episodes: those work on Episodes alone.
public sealed partial class ItemDetailViewModel
{
    // The listed episodes' multi-episode files read at once, at most.
    private const int MaxParallelReads = 3;

    private IReadOnlyList<DetailEpisodePlaceholder> _places = [];
    private CancellationTokenSource? _placing;

    /// <summary>
    /// The season's grid: <see cref="Episodes"/>, with the places of missing and upcoming episodes among them. It changes in
    /// place, so the cards that stay keep their elements, the keyboard focus and the scroll position.
    /// </summary>
    public ObservableCollection<DetailEpisodeEntry> EpisodeGrid { get; } = [];

    /// <summary>The placing the last load started, for tests.</summary>
    internal Task Placing { get; private set; } = Task.CompletedTask;

    // After the episodes show, on the UI thread. A newer load (Reset) and leaving the page (Deactivate) stop it, and so does
    // the load's own cancellation while the load still runs. Specials have no places: the calendar leaves season 0 out.
    private void StartPlacing(Uri server, string? seriesId, int? season, int generation, CancellationToken loadCancellation)
    {
        StopPlacing();
        if (_airings is null || string.IsNullOrWhiteSpace(seriesId) || season is not { } number || number <= 0)
        {
            return;
        }
        var run = CancellationTokenSource.CreateLinkedTokenSource(loadCancellation);
        _placing = run;
        Placing = PlaceAsync(server, seriesId, number, generation, run);
    }

    private void StopPlacing()
    {
        _placing?.Cancel();
        _placing?.Dispose();
        _placing = null;
    }

    private async Task PlaceAsync(Uri server, string seriesId, int season, int generation, CancellationTokenSource run)
    {
        var cancellationToken = run.Token;
        IReadOnlyList<DetailEpisodePlaceholder>? places = null;
        try
        {
            places = await PlacesAsync(server, seriesId, season, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Another title loaded, or the page was left.
        }
        catch (Exception ex)
        {
            // A mistake of the placing's own: the season shows as listed, and the log says why.
            _logger.LogWarning(ex, "Placing the missing and upcoming episodes of item {ItemId} stopped", ItemId);
        }
        // On the page's thread, and only while this is still the page's placing: Reset and Deactivate stop and replace it.
        OnUiThread(() =>
        {
            if (!ReferenceEquals(_placing, run))
            {
                return;
            }
            bool current = generation == _generation && !cancellationToken.IsCancellationRequested;
            _placing = null;
            run.Dispose();
            if (places is not null && current)
            {
                _places = places;
                ShowEpisodeGrid();
            }
        });
    }

    // Null when the calendar cannot be read: the season shows as listed.
    private async Task<IReadOnlyList<DetailEpisodePlaceholder>?> PlacesAsync(Uri server, string seriesId, int season, CancellationToken cancellationToken)
    {
        // What the profile may see decides what the calendar lists, so its answers are kept per profile.
        string? profileId = _session.ProfileId;
        var listed = _episodes;
        var last = listed.Where(e => e.EpisodeNumber > 0).MaxBy(e => e.EpisodeNumber);
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        string? ianaZone = CalendarText.IanaZone(zone, now);
        // Without a zone name the server reckons the days in UTC, and so must the windows.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, ianaZone is null ? TimeZoneInfo.Utc : zone).DateTime);
        var gaps = last is null ? [] : SeasonGaps.Find(listed);
        List<EpisodeAiring> airings = [];
        try
        {
            var reads = SeasonGaps.Windows(gaps, last, today)
                .Select(w => _airings!.GetAsync(server, profileId, seriesId, w.Start, w.End, ianaZone, cancellationToken)).ToList();
            foreach (var read in await Task.WhenAll(reads))
            {
                airings.AddRange(read);
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            _logger.LogWarning("Placing the missing and upcoming episodes of item {ItemId} failed: {Failure}", ItemId, ApiFailureText.Describe(ex));
            return null;
        }
        var planned = SeasonGaps.Places(season, gaps, last, airings, now, today, zone);
        // A server that lists a double episode's second half has no gap hiding one; for another, the episode before a place is
        // read, and one that cannot be read keeps its places out.
        if (SeasonGaps.ListsBothHalves(listed))
        {
            return [.. planned.Select(p => p.Place)];
        }
        var held = await HeldAsync(server, profileId, [.. planned.Select(p => p.HeldBy).OfType<Episode>().DistinctBy(e => e.ContentId)], cancellationToken);
        return [.. planned.Where(p => p.HeldBy is not { } by || held.GetValueOrDefault(by.ContentId) is { } spans && !spans.Contains(p.Place.EpisodeNumber))
            .Select(p => p.Place)];
    }

    // The numbers each episode's multi-episode file holds, null for one that cannot be read; a few episodes at a time.
    private async Task<Dictionary<string, IReadOnlyCollection<int>?>> HeldAsync(Uri server, string? profileId, IReadOnlyList<Episode> episodes,
        CancellationToken cancellationToken)
    {
        if (episodes.Count == 0)
        {
            return [];
        }
        using var slots = new SemaphoreSlim(MaxParallelReads);
        var reads = episodes.Select(async episode =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                return (episode.ContentId, Spans: await SpannedAsync(server, profileId, episode, cancellationToken));
            }
            finally
            {
                slots.Release();
            }
        }).ToList();
        return (await Task.WhenAll(reads)).ToDictionary(read => read.ContentId, read => read.Spans);
    }

    private async Task<IReadOnlyCollection<int>?> SpannedAsync(Uri server, string? profileId, Episode episode, CancellationToken cancellationToken)
    {
        if (_spans is null)
        {
            return null;
        }
        try
        {
            return await _spans.GetAsync(server, profileId, episode.ContentId, cancellationToken);
        }
        // ArgumentException: an episode id the client refuses to put in a path.
        catch (Exception ex) when (ex is ArgumentException || ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            _logger.LogWarning("Reading the files of episode {EpisodeId} failed, the places after it stay out: {Failure}", episode.ContentId,
                ApiFailureText.Describe(ex));
            return null;
        }
    }

    // The grid changes in place, as HomeRowViewModel.Take changes a row: an entry that stays keeps its element, a changed card
    // is replaced where it is, and the places come and go around them. Each place goes before the first episode numbered
    // after it; one whose episode the listing now has is gone.
    private void ShowEpisodeGrid()
    {
        var next = GridEntries();
        var keys = next.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        for (int i = EpisodeGrid.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(KeyOf(EpisodeGrid[i])))
            {
                EpisodeGrid.RemoveAt(i);
            }
        }
        for (int i = 0; i < next.Count; i++)
        {
            int at = IndexInGrid(KeyOf(next[i]), i);
            if (at < 0)
            {
                EpisodeGrid.Insert(i, next[i]);
                continue;
            }
            if (at != i)
            {
                EpisodeGrid.Move(at, i);
            }
            if (!Equals(EpisodeGrid[i], next[i]))
            {
                EpisodeGrid[i] = next[i];
            }
        }
        while (EpisodeGrid.Count > next.Count)
        {
            EpisodeGrid.RemoveAt(EpisodeGrid.Count - 1);
        }
    }

    private List<DetailEpisodeEntry> GridEntries()
    {
        var episodes = Episodes;
        List<DetailEpisodeEntry> entries = new(episodes.Count + _places.Count);
        if (_episodes.Count != episodes.Count)
        {
            entries.AddRange(episodes);
            return entries;
        }
        var listed = _episodes.Select(e => e.EpisodeNumber).ToHashSet();
        var places = _places.Where(p => !listed.Contains(p.EpisodeNumber)).ToList();
        int next = 0;
        for (int i = 0; i < episodes.Count; i++)
        {
            while (next < places.Count && places[next].EpisodeNumber < _episodes[i].EpisodeNumber)
            {
                entries.Add(places[next++]);
            }
            entries.Add(episodes[i]);
        }
        entries.AddRange(places.Skip(next));
        return entries;
    }

    private int IndexInGrid(string key, int from)
    {
        for (int i = from; i < EpisodeGrid.Count; i++)
        {
            if (KeyOf(EpisodeGrid[i]) == key)
            {
                return i;
            }
        }
        return -1;
    }

    private static string KeyOf(DetailEpisodeEntry entry) => entry switch
    {
        DetailEpisode episode => "episode " + episode.Id,
        DetailEpisodePlaceholder place => "place " + place.EpisodeNumber,
        _ => "",
    };
}
