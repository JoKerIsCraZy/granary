using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Settings;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;
using Granary.Tests.Settings;

namespace Granary.Tests.Browse;

/// <summary>A library's Next Up and Continue Watching rows blur an unwatched episode's still, as Home's do.</summary>
public sealed class LibrarySpoilerTests
{
    private const string SectionsPath = "api/v2/library/1/sections";

    private const string NextUpRows = """
        {
          "sections": [
            {
              "id": "next_up", "section_type": "next_up", "title": "Next Up",
              "featured": false, "item_limit": 20, "total_count": 1, "is_custom": false, "customized": false,
              "items": [
                {
                  "content_id": "episode-1-2", "type": "episode", "title": "Chapter 2", "status": "matched", "genres": [], "keywords": [],
                  "series_id": "series-1", "series_title": "Lupin", "season_number": 1, "episode_number": 2, "item_source": "next_up",
                  "backdrop_url": "/api/v2/artwork/episodes/2/backdrop.webp?exp=1&sig=b", "backdrop_is_episode_still": true,
                  "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false }
                }
              ]
            },
            {
              "id": "recently_added", "section_type": "recently_added", "title": "Recently Added",
              "featured": false, "item_limit": 20, "total_count": 12, "is_custom": false, "customized": false, "items": []
            }
          ]
        }
        """;

    private readonly LibraryHarness _harness = new();

    [Fact]
    public async Task NextUp_BlursAnUnwatchedEpisodesStill_AndFollowsTheSwitch()
    {
        _harness.Answer(SectionsPath, NextUpRows);
        _harness.Spoilers = await SpoilerSwitches.StartedAsync();
        var library = _harness.Create();
        await library.ActivateAsync("1");
        var row = library.Recommended.Rows[0];

        Assert.True(Assert.Single(row.Cards).BlursImage);
        Assert.All(library.Recommended.Rows[1].Cards, card => Assert.False(card.BlursImage));

        await _harness.Spoilers.SetHidesImagesAsync(false);

        Assert.Same(row, library.Recommended.Rows[0]);
        Assert.False(row.Cards[0].BlursImage);
    }

    [Fact]
    public async Task ARowMadeBeforeTheSwitchChanged_IsMadeAgain_ThoughAnotherRowArrivedSince()
    {
        var spoilers = EpisodeSpoilers.Off;
        var release = new TaskCompletionSource();
        _harness.Answer(SectionsPath, NextUpRows);
        _harness.Answer("api/v2/library/1/sections/recently_added/items", LibraryHarness.Held(release, LibraryPayloads.RecentlyAddedItems));
        var rows = new LibraryRowsViewModel(new CatalogApi(_harness.Silo.Http), _harness.Navigation, _harness.Logs.For<LibraryRowsViewModel>(),
            () => spoilers);
        var loading = rows.LoadAsync(ScriptedSilo.Base, "1", ImageParameters.None);
        Assert.False(rows.Rows[0].Cards[0].BlursImage);

        spoilers = new EpisodeSpoilers(HidesImages: true, HidesOverviews: true);
        release.SetResult();
        await loading;
        rows.ShowSpoilers();

        Assert.True(rows.Rows[0].Cards[0].BlursImage);
    }

    [Fact]
    public async Task ASwitchChangedWhileAway_ShowsOnTheNextVisit()
    {
        _harness.Answer(SectionsPath, NextUpRows);
        _harness.Spoilers = await SpoilerSwitches.StartedAsync(images: false);
        var library = _harness.Create();
        await library.ActivateAsync("1");
        library.Deactivate();

        await _harness.Spoilers.SetHidesImagesAsync(true);
        Assert.False(library.Recommended.Rows[0].Cards[0].BlursImage);

        await library.ActivateAsync("1");

        Assert.True(library.Recommended.Rows[0].Cards[0].BlursImage);
        Assert.Equal(1, _harness.Count(SectionsPath));
    }
}
