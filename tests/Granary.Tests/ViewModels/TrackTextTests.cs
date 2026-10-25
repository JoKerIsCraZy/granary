using System.Globalization;
using Granary.Core.Api.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>The audio menu's wording, as Silo's web player has it (web/src/lib/mediaFormat.ts, versionFormatUtils.ts).</summary>
public sealed class TrackTextTests
{
    [Theory]
    [InlineData("de", "German")]
    [InlineData("ger", "German")]
    [InlineData("deu", "German")]
    [InlineData("eng", "English")]
    [InlineData("fre", "French")]
    [InlineData("pt-BR", "Portuguese (Brazil)")]
    [InlineData("pt_BR", "Portuguese (Brazil)")]
    [InlineData("und", "Unknown language")]
    [InlineData("xx", "Unknown language (xx)")]
    [InlineData("english", "English")]
    [InlineData(" ", "")]
    [InlineData(null, "")]
    public void LanguageName_IsTheEnglishName(string? language, string expected) =>
        Assert.Equal(expected, TrackText.LanguageName(language));

    [Theory]
    [InlineData("eac3", null, null, "EAC3")]
    [InlineData("eac3", "Dolby Digital Plus + Dolby Atmos", null, "DD+ Atmos")]
    [InlineData("truehd", null, "TrueHD Atmos 7.1", "TrueHD Atmos")]
    [InlineData("ac3", null, "AC3 5.1 (from TrueHD Atmos)", "AC3")]
    [InlineData("dts", "DTS-HD MA", null, "DTS")]
    [InlineData("dts-hd ma", null, null, "DTS-HD")]
    [InlineData("aac", null, null, "AAC")]
    [InlineData("opus", null, null, "OPUS")]
    [InlineData("", null, null, "")]
    public void AudioCodec_IsTheFamily_WithAtmosOnlyWhereItCanBe(string codec, string? profile, string? title, string expected) =>
        Assert.Equal(expected, TrackText.AudioCodec(new WatchAudioTrack { Codec = codec, Profile = profile, Title = title }));

    [Theory]
    [InlineData(8, "7.1")]
    [InlineData(6, "5.1")]
    [InlineData(2, "stereo")]
    [InlineData(1, "1 ch")]
    [InlineData(0, "")]
    public void Channels_AreNamedAsOnTheWeb(int channels, string expected) => Assert.Equal(expected, TrackText.Channels(channels));

    [Fact]
    public void AnUntitledTrack_IsNamedByWhatTheFileSays()
    {
        Assert.Equal("Commentary", TrackText.AudioTitle(new WatchAudioTrack { Title = "Commentary", EmbeddedTitle = "Track 2" }));
        Assert.Equal("Track 2", TrackText.AudioTitle(new WatchAudioTrack { Title = " ", EmbeddedTitle = "Track 2" }));
        Assert.Equal("ger eac3 5.1", TrackText.AudioTitle(new WatchAudioTrack { Language = "ger", Codec = "eac3", Channels = 6 }));
        Assert.Equal("Audio", TrackText.AudioTitle(new WatchAudioTrack()));
    }

    [Fact]
    public void TheDetailLine_NamesTheLanguage_UnlessTheTitleDoes()
    {
        var track = new WatchAudioTrack { Language = "de", Layout = "5.1(side)", Bitrate = 640, SampleRate = 48000, BitDepth = 24 };

        Assert.Equal("German · 5.1(side) · 640 kbps · 48,000 Hz · 24-bit", TrackText.AudioMeta(track, "Deutsch", CultureInfo.InvariantCulture));
        Assert.Equal("5.1(side) · 640 kbps · 48,000 Hz · 24-bit", TrackText.AudioMeta(track, "german", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheMenu_HasARowPerTrack_TheOneThatPlaysChecked()
    {
        IReadOnlyList<WatchAudioTrack> tracks =
        [
            new() { Language = "eng", Codec = "eac3", Channels = 6, Default = true },
            new() { Language = "ger", Title = "Deutsch", Codec = "truehd", Profile = "Dolby TrueHD + Dolby Atmos", Channels = 8 },
        ];

        var rows = AudioTrackOption.For(tracks, selected: 1, CultureInfo.InvariantCulture);

        Assert.Equal(
            [
                new AudioTrackOption(0, "eng eac3 5.1", "English", "EAC3", "5.1", IsDefault: true, IsSelected: false),
                new AudioTrackOption(1, "Deutsch", "German", "TrueHD Atmos", "7.1", IsDefault: false, IsSelected: true),
            ],
            rows);
        Assert.Equal("eng eac3 5.1, EAC3 5.1, default, English", rows[0].AccessibleName);
    }

    [Fact]
    public void WithoutASelection_NoRowIsChecked() =>
        Assert.DoesNotContain(AudioTrackOption.For([new WatchAudioTrack(), new WatchAudioTrack()], selected: null), row => row.IsSelected);
}
