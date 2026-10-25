using Granary.Core.Api.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>The settings menu's rows, as Silo's web player builds them (QualityMenu, qualityOptionsFromPlanV3).</summary>
public sealed class PlayerSettingsOptionsTests
{
    private static readonly AvailableQuality[] Rungs =
    [
        new() { Label = "1080p-high", DisplayName = "1080p High", Height = 1080, BitrateKbps = 12000 },
        new() { Label = "720p-medium", DisplayName = "720p Medium", Height = 720, BitrateKbps = 4500 },
        new() { Label = "360p", Height = 360, BitrateKbps = 300 },
    ];

    [Fact]
    public void Quality_IsAutoThenTheRungs_WithAutoLitForAnAutoPreference()
    {
        var options = QualityOption.For(Rungs, "auto", "720p-medium", isLimited: false);

        Assert.Equal(
            [
                new QualityOption("auto", "Auto", "", true),
                new QualityOption("1080p-high", "1080p High", "12 Mbps", false),
                new QualityOption("720p-medium", "720p Medium", "4.5 Mbps", false),
                new QualityOption("360p", "360p", "300 kbps", false),
            ],
            options);
    }

    [Fact]
    public void Quality_LightsTheRungThePlanDelivers_OrNoneSoAutoStaysToPick()
    {
        Assert.True(QualityOption.For(Rungs, "720p-medium", "720p-medium", false).Single(o => o.IsSelected).Preference == "720p-medium");
        Assert.DoesNotContain(QualityOption.For(Rungs, "1080p", null, false), o => o.IsSelected);
        Assert.Equal("Auto · limited after buffering", QualityOption.For(Rungs, "auto", null, isLimited: true)[0].Label);
        Assert.Empty(QualityOption.For([], "auto", null, false));
    }

    [Fact]
    public void Versions_AreNamedByResolutionCodecAndHdr_WithThePlayingOneMarked()
    {
        WatchFileVersion[] files =
        [
            new() { FileId = "42", Resolution = "1080p", CodecVideo = "h264" },
            new() { FileId = "43", Resolution = "2160p", CodecVideo = "hevc", Hdr = true },
        ];

        Assert.Equal(
            [new VersionOption("42", "1080p H264", "Requested", false), new VersionOption("43", "2160p HEVC HDR", "Playing", true)],
            VersionOption.For(files, playing: "43", requested: "42"));
        Assert.Empty(VersionOption.For(files[..1], "42", "42"));
    }

    [Fact]
    public void Speeds_AreTheAppsStops_AndASavedSpeedShowsAtTheNearest()
    {
        Assert.Equal("1.25×", PlaybackSpeeds.Format(1.25));
        Assert.Equal(2, PlaybackSpeeds.NearestStop(1.05));
        Assert.Equal(8, PlaybackSpeeds.NearestStop(3));
    }
}
