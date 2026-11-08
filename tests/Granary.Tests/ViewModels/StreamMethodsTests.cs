using Granary.Core.Api.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>How the stream reaches the player, for the dot in the settings button's gear: the original streams, or re-encoded ones.</summary>
public sealed class StreamMethodsTests
{
    private static PlaybackPlan Plan(string delivery, params string[] serverTransformations) => new()
    {
        Delivery = delivery,
        Transformations = [.. serverTransformations.Select(name => new PlaybackTransformation { Name = name, Executor = "server" })],
    };

    [Fact]
    public void NoPlan_IsNoMethod() => Assert.Equal(StreamMethod.None, StreamMethods.Of(null));

    [Fact]
    public void TheOriginalFile_IsDirectPlay() =>
        Assert.Equal(StreamMethod.DirectPlay, StreamMethods.Of(Plan(PlaybackDeliveries.OriginalHttp)));

    [Theory]
    [InlineData(PlaybackDeliveries.ServerRemuxProgressive)]
    [InlineData(PlaybackDeliveries.ServerRemuxHls)]
    public void ANewContainerForTheOriginalStreams_IsDirectStream(string delivery) =>
        Assert.Equal(StreamMethod.DirectStream, StreamMethods.Of(Plan(delivery)));

    [Theory]
    [InlineData("server_dv7_to_hdr10")]
    [InlineData("server_dv7_to_dv81")]
    public void ADolbyVisionStrip_EncodesNothing_AndStaysDirectStream(string transformation) =>
        Assert.Equal(StreamMethod.DirectStream, StreamMethods.Of(Plan(PlaybackDeliveries.ServerRemuxHls, transformation)));

    [Theory]
    [InlineData(PlaybackDeliveries.ServerRemuxProgressive)]
    [InlineData(PlaybackDeliveries.ServerRemuxHls)]
    public void ACopiedVideoWithConvertedAudio_IsATranscode(string delivery) =>
        Assert.Equal(StreamMethod.Transcode, StreamMethods.Of(Plan(delivery, "audio_to_aac")));

    [Theory]
    [InlineData("video_to_h264")]
    [InlineData("video_to_hevc")]
    public void AnEncodedVideo_IsATranscode(string transformation) =>
        Assert.Equal(StreamMethod.Transcode, StreamMethods.Of(Plan(PlaybackDeliveries.ServerTranscodeHls, transformation)));

    [Fact]
    public void ToneMapping_EncodesTheVideo_AndIsATranscode() =>
        Assert.Equal(StreamMethod.Transcode, StreamMethods.Of(Plan(PlaybackDeliveries.ServerRemuxHls, "hdr_to_sdr_tonemap")));

    [Fact]
    public void AServerTransformationThisAppDoesNotKnow_CountsAsATranscode() =>
        Assert.Equal(StreamMethod.Transcode, StreamMethods.Of(Plan(PlaybackDeliveries.ServerRemuxProgressive, "server_future_filter")));

    [Fact]
    public void ATranscodeDelivery_IsATranscode_EvenWhenItNamesNoEncode() =>
        Assert.Equal(StreamMethod.Transcode, StreamMethods.Of(Plan(PlaybackDeliveries.ServerTranscodeHls)));

    [Fact]
    public void AnEncodeTheClientDoes_IsNotTheServers() =>
        Assert.Equal(StreamMethod.DirectStream, StreamMethods.Of(new PlaybackPlan
        {
            Delivery = PlaybackDeliveries.ServerRemuxHls,
            Transformations = [new PlaybackTransformation { Name = "audio_to_aac", Executor = "client" }],
        }));

    [Fact]
    public void ADeliveryThisAppDoesNotKnow_IsNoMethod() =>
        Assert.Equal(StreamMethod.None, StreamMethods.Of(Plan("server_future_delivery")));

    [Theory]
    [InlineData(StreamMethod.DirectPlay, "Direct Play")]
    [InlineData(StreamMethod.DirectStream, "Direct Streaming")]
    [InlineData(StreamMethod.Transcode, "Transcode")]
    [InlineData(StreamMethod.None, "")]
    public void EachMethod_HasPlaybackInfosWords(StreamMethod method, string label) =>
        Assert.Equal(label, StreamMethods.Label(method));
}
