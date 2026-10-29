using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.Browse;

/// <summary>The hero at the top of For You, fed by its recommendation rows as Home's is by its own.</summary>
public sealed class ForYouHeroTests
{
    private const string DiscoverPath = "api/v2/recommendations/discover";
    private const string TastePath = "api/v2/recommendations/taste-profile";

    private readonly LibraryHarness _harness = new();
    private readonly ManualTimeProvider _time = new();

    public ForYouHeroTests()
    {
        _harness.Answer(DiscoverPath, RecommendationPayloads.Discover);
        _harness.Answer(TastePath, RecommendationPayloads.Taste);
    }

    private async Task<ForYouViewModel> LoadedAsync()
    {
        var page = new ForYouViewModel(_harness.Session, new RecommendationsApi(_harness.Silo.Http),
            new ImageParameterCache(new ImagesApi(_harness.Silo.Http), _harness.Logs.For<ImageParameterCache>()), _harness.Navigation,
            new ImmediateDispatcher(), _time, _harness.Logs.For<ForYouViewModel>());
        await page.ActivateAsync(isReturn: false);
        return page;
    }

    [Fact]
    public async Task TheHero_ShowsTheFirstCardOfTheFirstRow()
    {
        var page = await LoadedAsync();

        var shown = Assert.IsType<BrowseHeroTitle>(page.Hero.Shown);
        Assert.Equal(("movie:heat-1995", "Heat", "1995"), (shown.Key, shown.Heading, shown.Meta));
    }

    [Fact]
    public async Task ACardPointedAt_LeadsTheHero_TheFirstAtOnce_ThenOnceItSettles()
    {
        var page = await LoadedAsync();
        var row = page.Rows[0];

        row.PointCardCommand!.Execute(row.Cards[1]);
        Assert.Equal("movie:alien-1979", page.Hero.Shown?.Key);

        row.PointCardCommand.Execute(row.Cards[0]);
        Assert.Equal("movie:alien-1979", page.Hero.Shown?.Key);
        _time.Advance(BrowseHeroViewModel.SettleTime);
        Assert.Equal("movie:heat-1995", page.Hero.Shown?.Key);
    }

    [Fact]
    public async Task ATitleWithoutArtwork_ShowsItsName_WithoutLogoOrBackdrop()
    {
        var page = await LoadedAsync();
        var row = page.Rows[0];

        row.PointCardCommand!.Execute(row.Cards[1]);

        var shown = Assert.IsType<BrowseHeroTitle>(page.Hero.Shown);
        Assert.Equal("Alien", shown.Heading);
        Assert.Null(shown.LogoUrl);
        Assert.Null(shown.BackdropUrl);
    }

    [Fact]
    public async Task AReload_KeepsTheTitleShown_WhileARowStillHasIt()
    {
        var page = await LoadedAsync();
        page.Rows[0].PointCardCommand!.Execute(page.Rows[0].Cards[1]);

        _harness.Answer(DiscoverPath, RecommendationPayloads.DiscoverShared);
        await page.ActivateAsync(isReturn: false);

        Assert.Equal("movie:alien-1979", page.Hero.Shown?.Key);
    }

    [Fact]
    public async Task NoRows_HideTheHero()
    {
        _harness.Answer(DiscoverPath, RecommendationPayloads.NoRows);

        var page = await LoadedAsync();

        Assert.Null(page.Hero.Shown);
    }
}
