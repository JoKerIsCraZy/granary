using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Profiles;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>A movie's or series' local extras, as the web's <c>ExtrasSection.tsx</c> shows them and the apps play them.</summary>
public sealed class ItemDetailExtrasTests
{
    private const string Thumbhash = "1QcSHQRnh493V4dIh4eXh1h4kJUI";

    // Two featurettes (one without a title) around a deleted scene, and a kind the client does not know, in the server's order.
    private const string Extras = """
        "extras": [
          { "content_id": "extra-71", "kind": "featurette", "title": "Making Masterplan", "duration_seconds": 742, "file_id": 9001,
            "still_url": "/api/v2/artwork/extras/71/still/w400.webp?exp=1&sig=s71", "still_thumbhash": "1QcSHQRnh493V4dIh4eXh1h4kJUI" },
          { "content_id": "extra-72", "kind": "deleted_scene", "title": "Alternate Ending", "duration_seconds": 3729, "file_id": 9002 },
          { "content_id": "extra-73", "kind": "featurette", "duration_seconds": 95, "file_id": 9003 },
          { "content_id": "extra-74", "kind": "interview", "title": "The Cast on Set", "file_id": 9004 }
        ],
        "subtitles": []
        """;

    private readonly ItemDetailFixture _fixture = new();

    private static Uri Art(string path) => new(ScriptedSilo.Base.AbsoluteUri.TrimEnd('/') + path);

    private static string WithExtras(string item, string extras = Extras)
    {
        string withExtras = item.Replace("\"subtitles\": []", extras, StringComparison.Ordinal);
        Assert.NotEqual(item, withExtras);
        return withExtras;
    }

    private ItemDetailFixture MovieWithExtras(string extras = Extras) =>
        _fixture.Movie().Get(ItemPath(MovieId), Ok(WithExtras(Movie, extras)));

    [Fact]
    public async Task Movie_GroupsItsExtrasByKind_InTheServersOrder()
    {
        var detail = await MovieWithExtras().LoadAsync(MovieId);

        Assert.True(detail.HasExtras);
        Assert.Equal(["Featurettes", "Deleted Scenes", "Other"], detail.ExtraGroups.Select(g => g.Label));
        Assert.Equal(["FEATURETTES", "DELETED SCENES", "OTHER"], detail.ExtraGroups.Select(g => g.Heading));
        Assert.Equal(["extra-71", "extra-73"], detail.ExtraGroups[0].Extras.Select(e => e.ContentId));
    }

    [Fact]
    public async Task AnExtra_ShowsItsStillTitleAndLength_AsTheWebWritesThem()
    {
        var detail = await MovieWithExtras().LoadAsync(MovieId);

        var making = detail.ExtraGroups[0].Extras[0];
        Assert.Equal("Making Masterplan", making.Title);
        Assert.Equal("12:22", making.Duration);
        Assert.Equal(Art("/api/v2/artwork/extras/71/still/w400.webp?exp=1&sig=s71"), making.StillUrl);
        Assert.Equal(Thumbhash, making.StillThumbhash);
        var ending = Assert.Single(detail.ExtraGroups[1].Extras);
        Assert.Equal("1:02:09", ending.Duration);
        Assert.Null(ending.StillUrl);
        Assert.Null(ending.StillThumbhash);
    }

    [Fact]
    public async Task AnExtraWithoutATitle_IsNamedByItsGroup_AsOnTheWeb()
    {
        var detail = await MovieWithExtras().LoadAsync(MovieId);

        var untitled = detail.ExtraGroups[0].Extras[1];
        Assert.Equal("Featurettes", untitled.Title);
        Assert.Equal("1:35", untitled.Duration);
    }

    [Fact]
    public async Task AnExtraWithoutALength_ShowsNoDuration()
    {
        var detail = await MovieWithExtras().LoadAsync(MovieId);

        var interview = Assert.Single(detail.ExtraGroups[2].Extras);
        Assert.Equal("The Cast on Set", interview.Title);
        Assert.Equal("", interview.Duration);
    }

    [Fact]
    public async Task ScreenReaders_HearAnExtrasTitleKindAndLength()
    {
        var detail = await MovieWithExtras().LoadAsync(MovieId);

        Assert.Equal(
            ["Making Masterplan, Featurette, 12:22, plays the extra", "Featurettes, 1:35, plays the extra"],
            detail.ExtraGroups[0].Extras.Select(e => e.AccessibleName));
        Assert.Equal("The Cast on Set, Extra, plays the extra", detail.ExtraGroups[2].Extras[0].AccessibleName);
    }

    [Fact]
    public async Task ATitleWithoutExtras_HasNoExtrasSection()
    {
        var detail = await _fixture.Movie().LoadAsync(MovieId);

        Assert.False(detail.HasExtras);
        Assert.Empty(detail.ExtraGroups);
    }

    [Fact]
    public async Task Series_ShowsItsExtrasToo()
    {
        var detail = await _fixture.Series().Get(ItemPath(SeriesId), Ok(WithExtras(Series))).LoadAsync(SeriesId);

        Assert.Equal(["Featurettes", "Deleted Scenes", "Other"], detail.ExtraGroups.Select(g => g.Label));
    }

    [Fact]
    public async Task AnExtraWithoutAContentId_IsLeftOut_ForItCannotPlay()
    {
        const string Unplayable = """
            "extras": [
              { "content_id": "", "kind": "featurette", "title": "Nowhere" },
              { "content_id": "extra-72", "kind": "deleted_scene", "title": "Alternate Ending" }
            ],
            "subtitles": []
            """;

        var detail = await MovieWithExtras(Unplayable).LoadAsync(MovieId);

        var group = Assert.Single(detail.ExtraGroups);
        Assert.Equal("Deleted Scenes", group.Label);
    }

    [Fact]
    public async Task AnExtra_PlaysInThePlayer_FromItsStart_AsTheAppsPlayIt()
    {
        var detail = await MovieWithExtras().LoadAsync(MovieId);

        detail.PlayExtraCommand.Execute(detail.ExtraGroups[1].Extras[0]);

        Assert.Equal((AppRoute.Player, (object?)PlayRequest.FromStart("extra-72")), _fixture.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task AnotherTitle_DropsTheLastOnesExtras()
    {
        MovieWithExtras().Series();
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(MovieId);
        Assert.True(detail.HasExtras);

        await detail.LoadCommand.ExecuteAsync(SeriesId);

        Assert.False(detail.HasExtras);
        Assert.Empty(detail.ExtraGroups);
    }

    [Theory]
    [InlineData("trailer", "Trailers")]
    [InlineData("teaser", "Teasers")]
    [InlineData("featurette", "Featurettes")]
    [InlineData("clip", "Clips")]
    [InlineData("behind_the_scenes", "Behind the Scenes")]
    [InlineData("bloopers", "Bloopers")]
    [InlineData("deleted_scene", "Deleted Scenes")]
    [InlineData("other", "Other")]
    [InlineData("interview", "Other")]
    [InlineData(null, "Other")]
    public void ExtraGroups_AreLabelledLikeTheWeb(string? kind, string expected) => Assert.Equal(expected, ExtraKinds.GroupLabel(kind));
}
