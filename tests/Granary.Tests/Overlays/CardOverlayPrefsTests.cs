using System.Text.Json;
using Granary.Core.Overlays;

namespace Granary.Tests.Overlays;

/// <summary>The profile's badge document, read and written as the web's (lib/overlays/schema.ts).</summary>
public sealed class CardOverlayPrefsTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void AVersion2Document_IsRead_AndEveryOtherBadgeKeepsItsDefault()
    {
        var prefs = CardOverlayPrefs.Parse(Json("""
            { "version": 2, "preset": "pill", "order": ["rating_imdb", "nonsense"],
              "items": { "rating_imdb": { "enabled": true, "position": "bottom-left", "accentColor": "#3b82f6", "showIcon": false } } }
            """));

        Assert.Equal(OverlayPresetId.Pill, prefs.Preset);
        Assert.Equal(["rating_imdb"], prefs.Order);
        Assert.Equal(new OverlayItemConfig(true, OverlayPosition.BottomLeft, "#3b82f6", false), prefs.ItemOf("rating_imdb"));
        Assert.Equal(new OverlayItemConfig(true, OverlayPosition.TopLeft), prefs.ItemOf("resolution"));
    }

    [Fact]
    public void TheLegacyString_AndAVersion1Map_AreRead()
    {
        var text = CardOverlayPrefs.Parse(Json("""
            "{\"version\":2,\"preset\":\"square\",\"order\":[],\"items\":{}}"
            """));
        var version1 = CardOverlayPrefs.Parse(Json("""{ "year": { "enabled": true, "position": "top-right" } }"""));

        Assert.Equal(OverlayPresetId.Square, text.Preset);
        Assert.Equal(OverlayPresetId.Classic, version1.Preset);
        Assert.Equal(new OverlayItemConfig(true, OverlayPosition.TopRight), version1.ItemOf("year"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void NothingOrSomethingMalformed_IsTheDefault(string? json)
    {
        Assert.Same(CardOverlayPrefs.Default, CardOverlayPrefs.ParseText(json));
    }

    [Fact]
    public void WritingAndReadingAgain_KeepsEveryChoice()
    {
        var prefs = (CardOverlayPrefs.Default with { Preset = OverlayPresetId.Vibrant, Order = ["year"] })
            .With("year", new OverlayItemConfig(true, OverlayPosition.BottomRight, "#ec4899", true));

        var json = prefs.ToJson();
        var again = CardOverlayPrefs.Parse(json);

        Assert.Equal(2, json.GetProperty("version").GetInt32());
        Assert.Equal("vibrant", json.GetProperty("preset").GetString());
        Assert.Equal("bottom-right", json.GetProperty("items").GetProperty("year").GetProperty("position").GetString());
        Assert.Equal(prefs.ItemOf("year"), again.ItemOf("year"));
        Assert.Equal(prefs.Order, again.Order);
    }

    [Fact]
    public void AnotherClientsChoice_ForAnIdGranaryDoesNotDraw_IsKept()
    {
        var prefs = CardOverlayPrefs.Parse(Json("""
            { "version": 2, "preset": "classic", "order": [], "items": { "imdb_top_250": { "enabled": true, "position": "top-left" } } }
            """));

        var written = prefs.ToJson().GetProperty("items").GetProperty("imdb_top_250");

        Assert.True(written.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void AServer_IsSentOnlyTheIdsItsManifestKnows()
    {
        var prefs = CardOverlayPrefs.Default;

        var older = prefs.ForServer(manifestRevision: 12, storedIds: new HashSet<string>());
        var newer = prefs.ForServer(manifestRevision: 15, storedIds: new HashSet<string>());
        var unknown = prefs.ForServer(manifestRevision: null, storedIds: new HashSet<string> { "advisory_age" });

        Assert.DoesNotContain("advisory_age", older.Items.Keys);
        Assert.DoesNotContain("request_status", older.Items.Keys);
        Assert.Contains("request_status", newer.Items.Keys);
        Assert.Contains("advisory_age", unknown.Items.Keys);
        Assert.DoesNotContain("request_status", unknown.Items.Keys);
        Assert.Contains("resolution", older.Items.Keys);
    }
}
