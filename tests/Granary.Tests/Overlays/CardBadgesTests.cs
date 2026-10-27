using Granary.Core.Overlays;

namespace Granary.Tests.Overlays;

/// <summary>Which badges a card shows and how they look, as the web's CardOverlays resolves them.</summary>
public sealed class CardBadgesTests
{
    private static readonly OverlayData Movie = new()
    {
        Resolution = "2160p",
        Hdr = "DV",
        Audio = "TrueHD Atmos",
        RatingImdb = 7.85,
        ContentRating = "PG-13",
        Year = 2024,
        Runtime = 135,
        ReleaseType = "REMUX",
    };

    private static CardOverlayPrefs Prefs(OverlayPresetId preset = OverlayPresetId.Classic, params (string Id, OverlayItemConfig Item)[] items) =>
        items.Aggregate(CardOverlayPrefs.Default with { Preset = preset }, (prefs, item) => prefs.With(item.Id, item.Item));

    [Fact]
    public void TheDefaults_ShowResolutionHdrAudio_TopLeft_AndTheReleaseType_BottomLeft()
    {
        var badges = CardBadges.Resolve(Movie, CardOverlayPrefs.Default);

        Assert.Equal(
            [("resolution", "4K", OverlayPosition.TopLeft), ("hdr", "DV", OverlayPosition.TopLeft), ("audio", "TRUEHD ATMOS", OverlayPosition.TopLeft),
             ("release_type", "REMUX", OverlayPosition.BottomLeft)],
            badges.Select(b => (b.Id, b.Text, b.Position)));
    }

    [Fact]
    public void TheCombinedBadge_StandsInForResolutionAndHdr()
    {
        var prefs = Prefs(items: ("resolution_hdr", new OverlayItemConfig(true, OverlayPosition.TopLeft)));

        var badges = CardBadges.Resolve(Movie, prefs);

        Assert.Contains(badges, b => b is { Id: "resolution_hdr", Text: "4K DV" });
        Assert.DoesNotContain(badges, b => b.Id is "resolution" or "hdr");
        Assert.True(prefs.IsSuppressed("hdr"));
    }

    [Fact]
    public void ACorner_StacksAtMostThree_InTheProfilesOrder()
    {
        var prefs = Prefs(items:
        [
            ("rating_imdb", new OverlayItemConfig(true, OverlayPosition.TopLeft)),
            ("year", new OverlayItemConfig(true, OverlayPosition.TopLeft)),
        ]) with { Order = ["year", "rating_imdb", "resolution"] };

        var corner = CardBadges.Resolve(Movie, prefs).Where(b => b.Position == OverlayPosition.TopLeft).Select(b => b.Id);

        Assert.Equal(["year", "rating_imdb", "resolution"], corner);
        Assert.Equal(CardBadges.MaxPerCorner, corner.Count());
    }

    [Fact]
    public void ABadgeWithoutData_IsLeftOut()
    {
        var badges = CardBadges.Resolve(new OverlayData { Resolution = "1080p" }, CardOverlayPrefs.Default);

        Assert.Equal("1080P", Assert.Single(badges).Text);
    }

    [Fact]
    public void AnIconSpellingTheWord_DropsTheLabel()
    {
        var prefs = Prefs(OverlayPresetId.Pill);

        var audio = Assert.Single(CardBadges.Resolve(new OverlayData { Audio = "Atmos" }, prefs));

        Assert.Equal(("atmos", ""), (audio.IconId, audio.Text));
        Assert.Equal("ATMOS", CardBadges.WordmarkOf("atmos"));
        Assert.Null(CardBadges.WordmarkOf("monitor"));
    }

    [Fact]
    public void Icons_FollowTheStyle_UnlessTheBadgeSaysOtherwise()
    {
        var data = new OverlayData { Resolution = "1080p" };
        var noIcon = Prefs(OverlayPresetId.Pill, ("resolution", new OverlayItemConfig(true, OverlayPosition.TopLeft, ShowIcon: false)));

        Assert.Null(Assert.Single(CardBadges.Resolve(data, CardOverlayPrefs.Default)).IconId);
        Assert.Equal("monitor", Assert.Single(CardBadges.Resolve(data, Prefs(OverlayPresetId.Pill))).IconId);
        Assert.Null(Assert.Single(CardBadges.Resolve(data, noIcon)).IconId);
    }

    [Fact]
    public void TheViewersColour_GoesBeforeTheBadgesOwn()
    {
        var data = new OverlayData { RatingImdb = 6.1 };
        var on = new OverlayItemConfig(true, OverlayPosition.BottomLeft);

        Assert.Equal("#f5c518", Assert.Single(CardBadges.Resolve(data, Prefs(items: ("rating_imdb", on)))).Accent);
        Assert.Equal("#3b82f6", Assert.Single(CardBadges.Resolve(data, Prefs(items: ("rating_imdb", on with { AccentColor = "#3b82f6" })))).Accent);
    }

    [Fact]
    public void APill_InIMDbGold_IsTheWebsColourMix()
    {
        var (background, foreground, border, leftOnly) = OverlayPreset.Of(OverlayPresetId.Pill).Colors("#f5c518");

        Assert.Equal(new BadgeColor(0xC2, 0x4F, 0x42, 0x1C), background);
        Assert.Equal(new BadgeColor(255, 255, 255, 255), foreground);
        Assert.Equal(BadgeColor.Rgba(255, 255, 255, 0.15), border);
        Assert.False(leftOnly);
    }

    [Fact]
    public void Square_MarksAColouredBadgeOnTheLeftOnly()
    {
        var (_, foreground, border, leftOnly) = OverlayPreset.Of(OverlayPresetId.Square).Colors("#10b981");

        Assert.Equal(BadgeColor.FromHex("#10b981"), foreground);
        Assert.Equal(BadgeColor.FromHex("#10b981"), border);
        Assert.True(leftOnly);
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData(null)]
    public void AColourThatIsNotHex_IsNone(string? hex) => Assert.Null(BadgeColor.FromHex(hex));
}
