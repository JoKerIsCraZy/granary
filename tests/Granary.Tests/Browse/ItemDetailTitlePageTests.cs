using Granary.Tests.Settings;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>The profile's Title Pages settings on a title's page, as the web's detail pages apply them.</summary>
public sealed class ItemDetailTitlePageTests
{
    private static readonly string AdvisedMovie =
        Movie.Replace("\"content_rating\": \"R\",", "\"content_rating\": \"R\", \"advisory_age\": 13, \"advisory_source\": \"commonsense\",", StringComparison.Ordinal);

    private static readonly string AdvisedSeries =
        Series.Replace("\"content_rating\": \"TV-MA\",", "\"content_rating\": \"TV-MA\", \"advisory_age\": 16, \"advisory_source\": \"mdblist\",", StringComparison.Ordinal);

    private static async Task<Core.ViewModels.ItemDetailViewModel> OpenAsync(ItemDetailFixture fixture, string id)
    {
        var detail = await fixture.LoadAsync(id);
        detail.Activate();
        return detail;
    }

    [Fact]
    public async Task TheAdvisoryAge_FollowsTheRating_WhenTheProfileAskedForIt()
    {
        var fixture = new ItemDetailFixture().Movie().Get(ItemPath(MovieId), Ok(AdvisedMovie));
        fixture.TitlePage = await TitlePageSwitches.StartedAsync(TitlePageSettingsTests.Answer(advisory: true));

        var detail = await OpenAsync(fixture, MovieId);

        Assert.Equal("2026 · 1h 49m · R · Common Sense 13+ · Action, Thriller", detail.MetaLine);
    }

    [Fact]
    public async Task ASeries_NamesItsAdvisorySource_AfterItsRating()
    {
        var fixture = new ItemDetailFixture().Series().Get(ItemPath(SeriesId), Ok(AdvisedSeries));
        fixture.TitlePage = await TitlePageSwitches.StartedAsync(TitlePageSettingsTests.Answer(advisory: true));

        var detail = await OpenAsync(fixture, SeriesId);

        Assert.Equal("2021–2026 · TV-MA · MDBList 16+ · 3 seasons · Crime, Drama, Mystery", detail.MetaLine);
    }

    [Fact]
    public async Task WithoutTheSwitch_NoAgeShows()
    {
        var fixture = new ItemDetailFixture().Movie().Get(ItemPath(MovieId), Ok(AdvisedMovie));
        fixture.TitlePage = await TitlePageSwitches.StartedAsync(TitlePageSettingsTests.Answer(advisory: false));

        var detail = await OpenAsync(fixture, MovieId);

        Assert.Equal("2026 · 1h 49m · R · Action, Thriller", detail.MetaLine);
    }

    [Fact]
    public async Task TurningTheSwitchOn_ShowsTheAge_OnAnOpenPage_WithTheSeasonsItCounted()
    {
        var fixture = new ItemDetailFixture().Series().Get(ItemPath(SeriesId), Ok(AdvisedSeries));
        var settings = await TitlePageSwitches.StartedAsync(TitlePageSettingsTests.Answer(advisory: false));
        fixture.TitlePage = settings;
        var detail = await OpenAsync(fixture, SeriesId);

        await settings.SetShowsAdvisoryAgeAsync(true);

        Assert.Equal("2021–2026 · TV-MA · MDBList 16+ · 3 seasons · Crime, Drama, Mystery", detail.MetaLine);
    }

    [Fact]
    public async Task AnUnknownSource_ShowsTheAgeAlone()
    {
        var fixture = new ItemDetailFixture().Movie()
            .Get(ItemPath(MovieId), Ok(AdvisedMovie.Replace("commonsense", "another", StringComparison.Ordinal)));
        fixture.TitlePage = await TitlePageSwitches.StartedAsync(TitlePageSettingsTests.Answer(advisory: true));

        var detail = await OpenAsync(fixture, MovieId);

        Assert.Equal("2026 · 1h 49m · R · 13+ · Action, Thriller", detail.MetaLine);
    }

    [Fact]
    public async Task AnEpisode_ShowsNoAdvisoryAge()
    {
        var fixture = new ItemDetailFixture().Episode()
            .Get(ItemPath(EpisodeId), Ok(Episode.Replace("\"type\": \"episode\",", "\"type\": \"episode\", \"advisory_age\": 16,", StringComparison.Ordinal)));
        fixture.TitlePage = await TitlePageSwitches.StartedAsync(TitlePageSettingsTests.Answer(advisory: true));

        var detail = await OpenAsync(fixture, EpisodeId);

        Assert.DoesNotContain("16+", detail.MetaLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALeftPage_NoLongerFollowsTheSwitch()
    {
        var fixture = new ItemDetailFixture().Movie().Get(ItemPath(MovieId), Ok(AdvisedMovie));
        var settings = await TitlePageSwitches.StartedAsync(TitlePageSettingsTests.Answer(advisory: false));
        fixture.TitlePage = settings;
        var detail = await OpenAsync(fixture, MovieId);
        detail.Deactivate();

        await settings.SetShowsAdvisoryAgeAsync(true);

        Assert.Equal("2026 · 1h 49m · R · Action, Thriller", detail.MetaLine);
    }
}
