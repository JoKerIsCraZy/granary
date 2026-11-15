using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// When the series' next episode airs, in the hero of the series' pages, Granary's own (docs/ui-design.md, "Detail pages"):
// the choice and the words are Silo's Apple app's for a monitored series' next download (NextAiring). It is read from the
// calendar's next 62 days, the windows every season's grid asks too, so while their answers are kept it costs no request
// of its own (SeriesAirings). The page does not wait for it, and it changes only when the page reads its data again: the
// load, and the reading after a change of the viewer's state (ItemDetailViewModel.Spoilers.cs), never with the clock.
public sealed partial class ItemDetailViewModel
{
    private CancellationTokenSource? _nextAiring;

    /// <summary>"Episode 6 on Thursday", "Season 4 starts tomorrow": the series' next airing; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextAiring))]
    public partial string NextAiringText { get; private set; } = "";

    public bool HasNextAiring => NextAiringText.Length > 0;

    /// <summary>The read of the next airing the last load or reading started, for tests.</summary>
    internal Task NextAiringRead { get; private set; } = Task.CompletedTask;

    // On the UI thread, once the item shows. A newer load (Reset), leaving the page (Deactivate) and the caller's token stop
    // it.
    private void StartNextAiring(Uri server, int generation, CancellationToken cancellationToken)
    {
        StopNextAiring();
        if (_airings is null || _item is not { } item || SeriesOf(item) is not { } seriesId)
        {
            return;
        }
        var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _nextAiring = run;
        NextAiringRead = ReadNextAiringAsync(server, seriesId, generation, run);
    }

    private void StopNextAiring()
    {
        _nextAiring?.Cancel();
        _nextAiring?.Dispose();
        _nextAiring = null;
    }

    // A series names its own next airing, a season or an episode its series'.
    private string? SeriesOf(CatalogItemDetail item) => item.Type switch
    {
        CatalogItemTypes.Series => _contentId,
        CatalogItemTypes.Season or CatalogItemTypes.Episode when !string.IsNullOrWhiteSpace(item.SeriesId) => item.SeriesId,
        _ => null,
    };

    private async Task ReadNextAiringAsync(Uri server, string seriesId, int generation, CancellationTokenSource run)
    {
        var cancellationToken = run.Token;
        string? text = null;
        try
        {
            var clock = CalendarNow.Of(_time);
            // What the profile may see decides what the calendar lists, so its answers are kept per profile.
            string? profileId = _session.ProfileId;
            var reads = SeasonGaps.TodaysWindows(clock.Today)
                .Select(w => _airings!.GetAsync(server, profileId, seriesId, w.Start, w.End, clock.TimeZone, cancellationToken)).ToList();
            var airings = (await Task.WhenAll(reads)).SelectMany(read => read);
            text = NextAiring.Find(airings, clock) is { } next ? NextAiring.Text(next, clock) : "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Another title loaded, or the page was left.
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            // The pill stays as it was: none after a load, the last reading's after a later one.
            _logger.LogWarning("Reading the next airing of item {ItemId} failed: {Failure}", ItemId, ApiFailureText.Describe(ex));
        }
        catch (Exception ex)
        {
            // A mistake of the read's own: the pill stays as it was, and the log says why.
            _logger.LogWarning(ex, "Reading the next airing of item {ItemId} stopped", ItemId);
        }
        // On the page's thread, and only while this is still the page's read: Reset and Deactivate stop and replace it.
        OnUiThread(() =>
        {
            if (!ReferenceEquals(_nextAiring, run))
            {
                return;
            }
            bool current = generation == _generation && !cancellationToken.IsCancellationRequested;
            _nextAiring = null;
            run.Dispose();
            if (text is not null && current)
            {
                NextAiringText = text;
            }
        });
    }
}
