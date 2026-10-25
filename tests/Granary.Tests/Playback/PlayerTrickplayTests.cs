using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>Seek-bar previews: which thumbnail shows a time, as the web player's trickplay.ts finds it.</summary>
public sealed class PlayerTrickplayTests
{
    private static readonly Uri Server = new("https://silo.example/silo/");

    private static TrickplayManifest Manifest(params TrickplaySheet[] sheets) => new()
    {
        FileId = "42",
        IntervalMs = 10_000,
        ThumbnailWidth = 320,
        ThumbnailHeight = 180,
        TileColumns = 10,
        TileRows = 10,
        ThumbnailCount = 150,
        Sheets = sheets,
        ExpiresAt = new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void TheSheets_AreInTheirOrder_AndResolvedAgainstTheServer()
    {
        var trickplay = PlayerTrickplay.Of(Manifest(
            new TrickplaySheet { Index = 1, Url = "https://cdn.example/1.jpg?sig=x" },
            new TrickplaySheet { Index = 0, Url = "/api/v2/artwork/a?exp=1&sig=y" }), Server)!;

        Assert.Equal("https://silo.example/silo/api/v2/artwork/a?exp=1&sig=y", trickplay.Sheets[0].AbsoluteUri);
        Assert.Equal("https://cdn.example/1.jpg?sig=x", trickplay.Sheets[1].AbsoluteUri);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(25, 0, 640, 0)]
    [InlineData(115, 0, 320, 180)]
    [InlineData(1005, 1, 0, 0)]
    [InlineData(99_999, 1, 2880, 720)]
    [InlineData(-3, 0, 0, 0)]
    public void ATime_ShowsItsIntervalsThumbnail(double seconds, int sheet, int x, int y)
    {
        var trickplay = PlayerTrickplay.Of(Manifest(
            new TrickplaySheet { Index = 0, Url = "/s/0.jpg" }, new TrickplaySheet { Index = 1, Url = "/s/1.jpg" }), Server)!;

        var tile = trickplay.TileAt(seconds);

        Assert.Equal((sheet, x, y), (tile!.Value.Sheet, tile.Value.X, tile.Value.Y));
    }

    [Fact]
    public void AThumbnailOnASheetTheServerDidNotList_HasNone()
    {
        var trickplay = PlayerTrickplay.Of(Manifest(new TrickplaySheet { Index = 0, Url = "/s/0.jpg" }), Server)!;

        Assert.Null(trickplay.TileAt(1005));
    }

    [Fact]
    public void AManifestWithoutAGrid_CannotBeShown()
    {
        Assert.Null(PlayerTrickplay.Of(Manifest() with { }, Server));
        Assert.Null(PlayerTrickplay.Of(Manifest(new TrickplaySheet { Url = "/s/0.jpg" }) with { TileColumns = 0 }, Server));
    }
}
