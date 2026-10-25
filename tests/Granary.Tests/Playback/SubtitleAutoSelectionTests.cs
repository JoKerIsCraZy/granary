using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>
/// The subtitle a playback starts with (docs/api-contract.md, 8.8.1), case for case as Android's AutoSubtitleResolverTest
/// pins it down.
/// </summary>
public sealed class SubtitleAutoSelectionTests
{
    private static WatchSubtitleTrack Track(string? language = null, string? codec = "subrip", string? title = null,
        bool forced = false, bool external = false, bool hearingImpaired = false) =>
        new() { Language = language, Codec = codec, Title = title, Forced = forced, External = external, HearingImpaired = hearingImpaired };

    private static int? Pick(IReadOnlyList<WatchSubtitleTrack> tracks, string? language, string mode, bool showForced = false,
        string? audio = null, WatchSubtitleSignature? signature = null) =>
        SubtitleAutoSelection.Resolve(SubtitleAutoSelection.FromCatalog(tracks), new SubtitlePreference(language, mode, showForced, audio, signature))
            ?.SelectionIndex;

    [Fact]
    public void ExternalTracksComeFirst_InTheCombinedOrdinals()
    {
        var candidates = SubtitleAutoSelection.FromCatalog([Track("eng"), Track("ger", external: true), Track("fre"), Track("spa", external: true)]);

        Assert.Equal([2, 0, 3, 1], candidates.Select(c => c.SelectionIndex));
    }

    [Fact]
    public void APlainExternalTextTrack_BeatsAnSdhEmbeddedBitmapOne() =>
        Assert.Equal(0, Pick([Track("eng", "hdmv_pgs_subtitle", "English (SDH)"), Track("eng", "srt", external: true)], "en", "always", showForced: true));

    [Theory]
    [InlineData("always")]
    [InlineData("auto")]
    public void ForcedSubtitlesOn_StillPreferTheFullDialogue(string mode) =>
        Assert.Equal(1, Pick([Track("eng", title: "Forced", forced: true), Track("eng"), Track("eng", title: "SDH")], "en", mode, showForced: true, audio: "ja"));

    [Fact]
    public void Forced_IsTheLastResort_WhenTheLanguageHasNothingElse() =>
        Assert.Equal(0, Pick([Track("eng", title: "Forced", forced: true)], "en", "always", showForced: true));

    [Fact]
    public void AnEmbeddedTextTrack_BeatsAnExternalOne() =>
        Assert.Equal(1, Pick([Track("eng", "srt", "English", external: true), Track("eng", "subrip", "English")], "en", "always"));

    [Fact]
    public void AnEmbeddedPgsTrack_BeatsAnExternalTextTrack() =>
        Assert.Equal(1, Pick([Track("eng", "srt", external: true), Track("eng", "hdmv_pgs_subtitle", "English")], "en", "always"));

    [Fact]
    public void AnEmbeddedBitmapTheServerBurnsIn_LosesToAnExternalTextTrack() =>
        Assert.Equal(0, Pick([Track("eng", "srt", external: true), Track("eng", "dvd_subtitle", "English")], "en", "always"));

    [Fact]
    public void AFullExternalTrack_BeatsAForcedEmbeddedOne() =>
        Assert.Equal(0, Pick([Track("eng", "srt", external: true), Track("eng", "subrip", "Forced", forced: true)], "en", "always", showForced: true));

    [Fact]
    public void ABurnInTrack_StillWinsWhenItIsTheOnlyMatch() =>
        Assert.Equal(1, Pick([Track("fre", "srt", external: true), Track("eng", "dvd_subtitle")], "en", "always"));

    [Fact]
    public void TheForcedTrackForMatchingAudio_PrefersTheEmbeddedOne() =>
        Assert.Equal(1, Pick([Track("eng", "srt", "Forced", forced: true, external: true), Track("eng", "subrip", "Forced", forced: true)],
            "en", "auto", showForced: true, audio: "eng"));

    [Fact]
    public void ThePreferredLanguage_IsShownForAudioInAnother() =>
        Assert.Equal(1, Pick([Track("eng"), Track("fre")], "fr", "auto", audio: "eng"));

    [Fact]
    public void AudioInThePreferredLanguage_ShowsNothingUnderAuto() =>
        Assert.Null(Pick([Track("eng")], "en", "auto", audio: "eng"));

    [Fact]
    public void AudioInThePreferredLanguage_ShowsItsForcedTrack_WithForcedSubtitlesOn() =>
        Assert.Equal(1, Pick([Track("eng"), Track("eng", forced: true)], "en", "auto", showForced: true, audio: "eng"));

    [Fact]
    public void ModeOff_ShowsNothing() => Assert.Null(Pick([Track("eng")], "en", "off"));

    [Theory]
    [InlineData("en", true, 1)]
    [InlineData("de", true, 2)]
    [InlineData("fr", true, null)]
    [InlineData("en", false, null)]
    [InlineData(null, true, null)]
    public void ModeOff_WithForcedOn_ShowsTheForcedTrackInTheAudiosLanguage(string? audio, bool showForced, int? expected) =>
        Assert.Equal(expected, Pick([Track("eng", title: "English"), Track("eng", title: "English Forced", forced: true),
            Track("ger", title: "Deutsch (erzwungen)", forced: true)], "en", "off", showForced, audio));

    [Fact]
    public void AnEmptyLanguage_MeansNoSubtitles() => Assert.Null(Pick([Track("eng")], "", "always"));

    [Fact]
    public void NoPreferenceUnderAuto_ShowsNothing() => Assert.Null(Pick([Track("eng")], null, "auto"));

    [Fact]
    public void AlwaysWithoutAPreference_PrefersFullDialogue() =>
        Assert.Equal(1, Pick([Track("fre", forced: true), Track("fre")], null, "always"));

    [Fact]
    public void FullDialogue_BeatsSdh() =>
        Assert.Equal(1, Pick([Track("eng", title: "English SDH"), Track("eng")], "en", "auto", audio: "jpn"));

    [Theory]
    [InlineData("dvb_subtitle")]
    [InlineData("vobsub")]
    public void ABitmapTheServerBurnsIn_IsSkippedForATextTrack(string bitmap) =>
        Assert.Equal(1, Pick([Track("fre", bitmap), Track("fre", "subrip")], "fr", "auto", audio: "eng"));

    [Fact]
    public void HiInATitle_IsNotHearingImpaired() =>
        Assert.Equal(0, Pick([Track("eng", title: "EN - HI"), Track("eng")], "en", "auto", audio: "jpn"));

    [Fact]
    public void NoTracks_NoSubtitle() => Assert.Null(Pick([], "en", "always"));

    [Fact]
    public void TheSeriesChoice_WinsOverTheLanguageRules()
    {
        var signature = new WatchSubtitleSignature { Source = "embedded", Language = "en", Codec = "subrip", Label = "SDH", HearingImpaired = true };

        Assert.Equal(1, Pick([Track("eng"), Track("eng", title: "SDH")], "de", "always", signature: signature));
    }

    [Fact]
    public void TheSeriesChoice_DoesNotOverrideOff()
    {
        var signature = new WatchSubtitleSignature { Source = "embedded", Language = "eng", Codec = "subrip" };

        Assert.Null(Pick([Track("eng")], "en", "off", signature: signature));
    }

    [Fact]
    public void WithoutItsExactTrack_TheSeriesChoiceKeepsItsTraits()
    {
        // Watched with SDH: another episode whose SDH track has another title still starts with SDH.
        var signature = new WatchSubtitleSignature { Source = "embedded", Language = "eng", Codec = "subrip", Label = "English SDH", HearingImpaired = true };

        Assert.Equal(1, Pick([Track("eng"), Track("eng", title: "SDH")], "en", "always", signature: signature));
    }

    [Fact]
    public void ASavedExternalTrack_IsFoundByItsFileName_AsTheServerLabelsIt()
    {
        var external = new WatchSubtitleTrack { Language = "ger", Codec = "srt", External = true, FileName = "/media/tv/Ted/S01E01.de.srt" };
        var signature = new WatchSubtitleSignature { Source = "external", Language = "ger", Codec = "srt", Label = "S01E01.de.srt" };

        Assert.Equal(0, Pick([Track("ger"), external], "en", "always", signature: signature));
    }

    [Fact]
    public void AStart_TakesTheAudioThatPlays_AndTheEffectiveFields()
    {
        var version = new WatchFileVersion
        {
            FileId = "7",
            EffectiveAudioTrackIndex = 1,
            AudioTracks = [new() { Language = "eng" }, new() { Language = "ger" }],
            SubtitleTracks = [Track("ger", forced: true), Track("ger")],
        };
        // German audio under auto with German preferred: the forced German track only.
        var detail = new WatchDetail { EffectiveSubtitleLanguage = "de" };

        Assert.Equal(0, SubtitleAutoSelection.ForStart(detail, version));
        Assert.Null(SubtitleAutoSelection.ForStart(detail with { EffectiveShowForcedSubtitles = false }, version));
        Assert.Equal(1, SubtitleAutoSelection.ForStart(detail, version with { EffectiveAudioTrackIndex = 0 }));
        Assert.Null(SubtitleAutoSelection.ForStart(new WatchDetail(), version));
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("fra", "fr")]
    [InlineData("fre", "fr")]
    [InlineData("ger", "de")]
    [InlineData("pt-BR", "pt")]
    [InlineData("ita", "it")]
    [InlineData("fil", "fil")]
    [InlineData("und", null)]
    [InlineData(" ", null)]
    public void Languages_FoldToTheirPrimaryIso6391Code(string language, string? key) => Assert.Equal(key, TrackLanguages.Key(language));
}
