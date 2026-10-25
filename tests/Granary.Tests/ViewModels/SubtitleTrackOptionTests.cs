using Granary.Core.Api.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>The subtitle menu's rows, as Silo's web player words them (SubtitleMenu, subtitleCodecs.ts).</summary>
public sealed class SubtitleTrackOptionTests
{
    private static SubtitleInventoryItem Item(int index, string source, string? language, string? codec, string? label = null) =>
        new() { TrackId = $"file:42:subtitle:{index}", CombinedIndex = index, Source = source, Language = language, Codec = codec, Label = label };

    [Fact]
    public void OffComesFirst_ThenEmbeddedExternalDownloaded()
    {
        var rows = SubtitleTrackOption.For(
            [Item(0, "external", "ger", "webvtt", "Deutsch (extern)"), Item(1, "embedded", "eng", "subrip", "English"), Item(2, "downloaded", "spa", "srt")],
            selected: "file:42:subtitle:0");

        Assert.Equal(
            [
                new SubtitleTrackOption(null, "Off", "", "", "", IsSelected: false),
                new SubtitleTrackOption("file:42:subtitle:1", "English", "SRT", "Embedded", "", IsSelected: false),
                new SubtitleTrackOption("file:42:subtitle:0", "German", "VTT", "External", "Deutsch (extern)", IsSelected: true),
                new SubtitleTrackOption("file:42:subtitle:2", "Spanish", "SRT", "Downloaded", "", IsSelected: false),
            ],
            rows);
        Assert.Equal("German, VTT, External, Deutsch (extern)", rows[2].AccessibleName);
    }

    [Fact]
    public void WithoutASelection_OffIsChecked() =>
        Assert.True(SubtitleTrackOption.For([Item(0, "embedded", "eng", "ass")], selected: null)[0].IsSelected);

    [Theory]
    [InlineData("ass", "ASS")]
    [InlineData("ssa", "ASS")]
    [InlineData("subrip", "SRT")]
    [InlineData("webvtt", "VTT")]
    [InlineData("hdmv_pgs_subtitle", "PGS")]
    [InlineData("dvd_subtitle", "DVD")]
    [InlineData("dvb_subtitle", "DVB")]
    [InlineData("mov_text", "")]
    [InlineData(null, "")]
    public void TheFormatChip_NamesTheKnownFormats(string? codec, string expected) => Assert.Equal(expected, TrackText.SubtitleFormat(codec));

    [Theory]
    [InlineData("SDH", "SDH")]
    [InlineData("eng", "")]
    [InlineData("English", "")]
    [InlineData("SubRip", "")]
    [InlineData("srt", "")]
    [InlineData(" ", "")]
    [InlineData(null, "")]
    public void TheDetailLine_OnlyShowsALabelThatSaysMore(string? label, string expected) =>
        Assert.Equal(expected, TrackText.SubtitleDetail(label, "eng", "English", "subrip"));

    [Fact]
    public void ATrackWithoutALanguage_IsUnknown() => Assert.Equal("Unknown", TrackText.SubtitleLanguage(null));
}
