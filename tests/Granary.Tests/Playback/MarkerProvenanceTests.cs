using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>The line above the skip pill, as the web's lib/markerProvenance.ts (and its tests) words it.</summary>
public sealed class MarkerProvenanceTests
{
    private static readonly MarkerProvenance FromPlugin = new("plugin", "plugin:4:introdb", 0.2);

    [Theory]
    [InlineData("plugin:4:introdb")]
    [InlineData("plugin:11:introdb")]
    public void APluginProvider_IsNamedByItsCapability_NotItsInstallation(string provider)
    {
        // A plugin marker stores the provider with the server's own installation id, which means nothing to a viewer.
        Assert.Equal("TheIntroDB", new MarkerProvenance("plugin", provider, null).SourceLabel);
    }

    [Theory]
    [InlineData("scanner", null, "Detected")]
    [InlineData("manual", null, "Edited")]
    [InlineData("plugin", "built_in", "Detected")]
    [InlineData("online", "introdb", "TheIntroDB")]
    public void TheOtherSources_AreNamedForWhatTheyAre(string source, string? provider, string expected)
    {
        Assert.Equal(expected, new MarkerProvenance(source, provider, null).SourceLabel);
    }

    [Theory]
    [InlineData("plugin", "some.other.plugin", "some.other.plugin")]
    [InlineData("online", null, "Online")]
    [InlineData("online", "  ", "Online")]
    [InlineData(null, "introdb", "TheIntroDB")]
    public void AnUnknownProvider_ShowsItsOwnId_RatherThanNothing(string? source, string? provider, string expected)
    {
        // The line answers "where did this come from?": a blank answers nothing.
        Assert.Equal(expected, new MarkerProvenance(source, provider, null).SourceLabel);
    }

    [Fact]
    public void NothingKnown_NamesNoSource()
    {
        Assert.Null(MarkerProvenance.None.SourceLabel);
        Assert.Null(new MarkerProvenance("s3", null, null).SourceLabel);
    }

    [Theory]
    [InlineData(0.2, "20%")]
    [InlineData(0.15, "15%")]
    [InlineData(1, "100%")]
    [InlineData(0.125, "13%")]
    [InlineData(0, "0%")]
    public void AConfidence_IsAWholePercentage(double confidence, string expected)
    {
        // A rated 0 is a rating: the server leaves the field out for a marker nobody rated.
        Assert.Equal(expected, new MarkerProvenance(null, null, confidence).ConfidencePercent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AMissingConfidence_IsNothingToShow_NotZeroPercent(double? confidence)
    {
        Assert.Null(new MarkerProvenance("scanner", null, confidence).ConfidencePercent);
    }

    [Theory]
    [InlineData(1.5, "100%")]
    [InlineData(-0.2, "0%")]
    public void AConfidenceOutOfRange_IsClamped(double confidence, string expected)
    {
        Assert.Equal(expected, new MarkerProvenance(null, null, confidence).ConfidencePercent);
    }

    [Theory]
    [InlineData(true, false, "20%")]
    [InlineData(false, true, "TheIntroDB")]
    [InlineData(true, true, "20% · TheIntroDB")]
    [InlineData(false, false, null)]
    public void TheLine_ShowsTheConfidenceTheSourceOrBoth_AsTheSettingsSay(bool confidence, bool source, string? expected)
    {
        Assert.Equal(expected, FromPlugin.Line(confidence, source));
    }

    [Fact]
    public void AnUnratedMarker_ShowsItsSourceAlone()
    {
        // With only the confidence switch on, the pill keeps the shape it had before.
        var unrated = FromPlugin with { Confidence = null };

        Assert.Null(unrated.Line(showConfidence: true, showSource: false));
        Assert.Equal("TheIntroDB", unrated.Line(showConfidence: true, showSource: true));
    }

    [Fact]
    public void NothingKnown_DrawsNoLine()
    {
        Assert.Null(MarkerProvenance.None.Line(showConfidence: true, showSource: true));
    }

    [Fact]
    public void AWatchMarker_GivesItsProvenance()
    {
        Assert.Equal(FromPlugin, MarkerProvenance.Of(new WatchMarker(0, 90) { Source = "plugin", Provider = "plugin:4:introdb", Confidence = 0.2 }));
        Assert.Equal(MarkerProvenance.None, MarkerProvenance.Of(null));
    }
}
