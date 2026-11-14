using System.Text;
using System.Text.Json;
using Granary.Core.Remote;

namespace Granary.Tests.Remote;

/// <summary>
/// The messages of Silo's phone remote in camelCase in the envelope <c>{"type", "v": 2, "&lt;kind&gt;": {…}}</c>, against
/// the shapes of silo-android's <c>SiloCastMessageTest.kt</c> and silo-apple's <c>SiloControlWireCompatibilityTests.swift</c>
/// with sample values. Frames as a phone sends them put the payload before <c>"v"</c>.
/// </summary>
public sealed class SiloCastMessagesTests
{
    private static readonly string ServerId = SiloCastServerId.From(new Uri("https://silo.example"));

    private static readonly ReceiverIdentity Receiver =
        new("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", "Elena's PC", ServerId, "Example Server", "deploy-1");

    [Fact]
    public void APhonesHello_IsRead()
    {
        var message = Decode(
            $$$"""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1","serverId":"{{{ServerId}}}","serverName":"Example Server","supportedVersions":[2],"serverIdentity":"deploy-1","features":["close_reason","resume_token","replace_confirmation"]},"v":2}""");

        var hello = Assert.IsType<SiloCastMessage.PhoneHello>(message);
        Assert.Equal("phone", hello.Role);
        Assert.Equal("Elena's phone", hello.DeviceName);
        Assert.Equal("phone-1", hello.DeviceId);
        Assert.Equal(ServerId, hello.ServerId);
        Assert.Equal("Example Server", hello.ServerName);
        Assert.Equal("deploy-1", hello.ServerIdentity);
        Assert.Equal([2], hello.SupportedVersions);
        Assert.Equal(["close_reason", "replace_confirmation", "resume_token"], hello.Features.Order(StringComparer.Ordinal));
        Assert.False(hello.Passive);
    }

    [Fact]
    public void APassivePhonesHello_SaysSo_AndAHelloWithoutFeaturesHasNone()
    {
        var passive = Assert.IsType<SiloCastMessage.PhoneHello>(Decode(
            """{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":[1,2],"passive":true},"v":2}"""));

        Assert.True(passive.Passive);
        Assert.Empty(passive.Features);
        Assert.Null(passive.ServerId);
        Assert.Equal([1, 2], passive.SupportedVersions);
    }

    [Theory]
    [InlineData("""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","supportedVersions":[2]},"v":2}""")]
    [InlineData("""{"type":"hello","hello":{"role":"phone","deviceId":"phone-1","supportedVersions":[2]},"v":2}""")]
    [InlineData("""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1"},"v":2}""")]
    [InlineData("""{"type":"hello","hello":{"deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":[2]},"v":2}""")]
    [InlineData("""{"type":"hello","hello":{"role":"remote","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":[2]},"v":2}""")]
    [InlineData("""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":["2"]},"v":2}""")]
    [InlineData("""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":[2],"features":[null]},"v":2}""")]
    [InlineData("""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":7,"supportedVersions":[2]},"v":2}""")]
    [InlineData("""{"type":"hello","hello":null,"v":2}""")]
    [InlineData("""{"type":"hello","v":2}""")]
    public void AHelloWithoutItsRequiredFields_IsMalformed(string frame) => Assert.Null(Decode(frame));

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("play_pause")]
    [InlineData("stop")]
    [InlineData("seek")]
    [InlineData("set_volume")]
    [InlineData("set_muted")]
    [InlineData("select_audio_track")]
    [InlineData("select_subtitle_track")]
    [InlineData("select_subtitle_track off")]
    [InlineData("set_playback_speed")]
    [InlineData("set_quality")]
    [InlineData("set_video_gravity")]
    [InlineData("set_subtitle_sync_ms")]
    [InlineData("set_subtitle_position")]
    [InlineData("play_next")]
    public void EveryControl_IsReadWithItsArgument(string command)
    {
        (string payload, SiloCastMessage.Control expected) = command switch
        {
            "play" => ("""{"name":"play"}""", Control("play")),
            "pause" => ("""{"name":"pause"}""", Control("pause")),
            "play_pause" => ("""{"name":"play_pause"}""", Control("play_pause")),
            "stop" => ("""{"name":"stop"}""", Control("stop")),
            "seek" => ("""{"name":"seek","seconds":1798.292724609375}""", Control("seek") with { Seconds = 1798.292724609375 }),
            "set_volume" => ("""{"name":"set_volume","volume":0.4}""", Control("set_volume") with { Volume = 0.4 }),
            "set_muted" => ("""{"name":"set_muted","enabled":true}""", Control("set_muted") with { Enabled = true }),
            "select_audio_track" => ("""{"name":"select_audio_track","trackId":1}""", Control("select_audio_track") with { TrackId = 1 }),
            "select_subtitle_track" => ("""{"name":"select_subtitle_track","trackId":3}""", Control("select_subtitle_track") with { TrackId = 3 }),
            "select_subtitle_track off" => ("""{"name":"select_subtitle_track"}""", Control("select_subtitle_track")),
            "set_playback_speed" => ("""{"name":"set_playback_speed","speed":1.25}""", Control("set_playback_speed") with { Speed = 1.25 }),
            "set_quality" => ("""{"name":"set_quality","value":"auto"}""", Control("set_quality") with { Value = "auto" }),
            "set_video_gravity" => ("""{"name":"set_video_gravity","value":"fill"}""", Control("set_video_gravity") with { Value = "fill" }),
            "set_subtitle_sync_ms" => ("""{"name":"set_subtitle_sync_ms","milliseconds":-250}""", Control("set_subtitle_sync_ms") with { Milliseconds = -250 }),
            "set_subtitle_position" => ("""{"name":"set_subtitle_position","value":"bottom"}""", Control("set_subtitle_position") with { Value = "bottom" }),
            "play_next" => ("""{"name":"play_next"}""", Control("play_next")),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        var message = Decode($$$"""{"type":"control","control":{{{payload}}},"v":2}""");

        Assert.Equal(expected, message);
        Assert.Contains(expected.Name, SiloCastControls.All);
    }

    [Fact]
    public void AnUnknownControlName_IsSkipped_ButAControlWithoutAName_OrWithAnArgumentOfTheWrongKind_IsMalformed()
    {
        // A retired command old phones still send, with an argument nothing checks.
        Assert.Equal(new SiloCastMessage.Unknown("control"), Decode("""{"type":"control","control":{"name":"set_hdr_enabled","enabled":"yes"},"v":2}"""));

        Assert.Null(Decode("""{"type":"control","control":{"enabled":true},"v":2}"""));
        Assert.Null(Decode("""{"type":"control","control":{"name":"seek","seconds":"10"},"v":2}"""));
        Assert.Null(Decode("""{"type":"control","control":{"name":"select_audio_track","trackId":1.5},"v":2}"""));
        Assert.Null(Decode("""{"type":"control","control":"seek","v":2}"""));
    }

    [Fact]
    public void AnOffer_IsRead_AndItsAddressesAreNotKept()
    {
        var message = Decode(
            $$$"""{"type":"handoff_offer","handoffOffer":{"requestId":"req-1","serverId":"{{{ServerId}}}","serverURL":"https://silo.example","serverName":"Example Server","profileId":"7","profileName":"Elena","title":"Example Movie","serverIdentity":"deploy-1","serverEndpoints":[{"url":"https://elsewhere.example","kind":"public"}],"resumeToken":"token-1","replaceConfirmed":true,"replacingContentId":"movie-1"},"v":2}""");

        var offer = Assert.IsType<SiloCastMessage.HandoffOffer>(message);
        Assert.Equal(new SiloCastMessage.HandoffOffer("req-1", ServerId, "deploy-1", "7", ReplaceConfirmed: true, ReplacingContentId: "movie-1"), offer);
        // Only what Granary reads is kept: no address, no endpoint, no token, nothing to contact.
        Assert.DoesNotContain(typeof(SiloCastMessage.HandoffOffer).GetProperties(), property =>
            property.Name.Contains("Url", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnOfferWithoutWhatItRequires_IsMalformed_AndOneWithoutConsentIsNotConfirmed()
    {
        var plain = Assert.IsType<SiloCastMessage.HandoffOffer>(Decode(
            $$$"""{"type":"handoff_offer","handoffOffer":{"requestId":"req-2","serverId":"{{{ServerId}}}","serverURL":"https://silo.example","profileId":"7"},"v":2}"""));
        Assert.False(plain.ReplaceConfirmed);
        Assert.Null(plain.ServerIdentity);
        Assert.Null(plain.ReplacingContentId);

        // As Silo's Roku app checks an offer (protocol.bs validOffer).
        Assert.Null(Decode($$$"""{"type":"handoff_offer","handoffOffer":{"requestId":"req-2","serverId":"{{{ServerId}}}","profileId":"7"},"v":2}"""));
        Assert.Null(Decode("""{"type":"handoff_offer","handoffOffer":{"requestId":"req-2","serverURL":"https://silo.example","profileId":"7"},"v":2}"""));
        Assert.Null(Decode($$$"""{"type":"handoff_offer","handoffOffer":{"requestId":"req-2","serverId":"{{{ServerId}}}","serverURL":"https://silo.example","profileId":7},"v":2}"""));
        Assert.Null(Decode($$$"""{"type":"handoff_offer","handoffOffer":{"requestId":"req-2","serverId":"{{{ServerId}}}","serverURL":"https://silo.example","profileId":"7","replaceConfirmed":"yes"},"v":2}"""));
    }

    [Fact]
    public void ALaunch_IsReadWithItsResumePosition()
    {
        var message = Decode(
            $$$"""{"type":"launch","launch":{"serverId":"{{{ServerId}}}","playback":{"contentId":"movie-1","startFromBeginning":false,"resumePosition":4747.284}},"v":2}""");

        var launch = Assert.IsType<SiloCastMessage.Launch>(message);
        Assert.Equal(ServerId, launch.ServerId);
        Assert.Equal(new LaunchPlayback("movie-1", null, null, null, StartFromBeginning: false, ResumePosition: 4747.284, PinsVersion: false), launch.Playback);
    }

    [Fact]
    public void ALaunchWithAVersionAndTracks_IsRead()
    {
        var message = Decode(
            $$$"""{"type":"launch","launch":{"serverId":"{{{ServerId}}}","playback":{"contentId":"movie-1","fileId":42,"audioTrackIndex":1,"subtitleTrackIndex":-1,"startFromBeginning":true,"libraryId":8,"pinsVersion":true}},"v":2}""");

        var launch = Assert.IsType<SiloCastMessage.Launch>(message);
        Assert.Equal(new LaunchPlayback("movie-1", "42", 1, -1, StartFromBeginning: true, ResumePosition: null, PinsVersion: true), launch.Playback);
    }

    [Fact]
    public void ALaunchsTrackOutsideTheRange_IsDropped_ButOneWithoutWhatItRequires_IsMalformed()
    {
        var launch = Assert.IsType<SiloCastMessage.Launch>(Decode(
            $$$"""{"type":"launch","launch":{"serverId":"{{{ServerId}}}","playback":{"contentId":"movie-1","audioTrackIndex":4294967296,"startFromBeginning":false}},"v":2}"""));
        Assert.Null(launch.Playback.AudioTrackIndex);

        Assert.Null(Decode($$$"""{"type":"launch","launch":{"serverId":"{{{ServerId}}}","playback":{"contentId":"movie-1"}},"v":2}"""));
        Assert.Null(Decode($$$"""{"type":"launch","launch":{"serverId":"{{{ServerId}}}","playback":{"startFromBeginning":false}},"v":2}"""));
        Assert.Null(Decode("""{"type":"launch","launch":{"playback":{"contentId":"movie-1","startFromBeginning":false}},"v":2}"""));
        Assert.Null(Decode($$$"""{"type":"launch","launch":{"serverId":"{{{ServerId}}}","playback":{"contentId":"movie-1","fileId":4.2,"startFromBeginning":false}},"v":2}"""));
        Assert.Null(Decode($$$"""{"type":"launch","launch":{"serverId":"{{{ServerId}}}"},"v":2}"""));
    }

    [Fact]
    public void APresence_APhonesCancel_ACloseAndTheHeartbeat_AreRead()
    {
        Assert.Equal(new SiloCastMessage.Presence(false), Decode("""{"type":"presence","presence":{"passive":false},"v":2}"""));
        Assert.Null(Decode("""{"type":"presence","presence":{},"v":2}"""));
        Assert.Equal(new SiloCastMessage.HandoffCancelled("req-1"),
            Decode("""{"type":"handoff_cancel","handoffCancel":{"requestId":"req-1","reason":"controller_cancelled"},"v":2}"""));
        Assert.Null(Decode("""{"type":"handoff_cancel","handoffCancel":{"requestId":"req-1"},"v":2}"""));
        Assert.Equal(new SiloCastMessage.Ping(), Decode("""{"type":"ping","v":2}"""));
        Assert.Equal(new SiloCastMessage.Pong(), Decode("""{"type":"pong","v":2}"""));
        Assert.Equal(new SiloCastMessage.Close("tv_disconnected"), Decode("""{"type":"close","close":{"reason":"tv_disconnected"},"v":2}"""));
        Assert.Equal(new SiloCastMessage.Close(null), Decode("""{"type":"close","v":2}"""));
        // A close's payload is read leniently, as the phones read it: a malformed one is no reason, never a broken frame.
        Assert.Equal(new SiloCastMessage.Close(null), Decode("""{"type":"close","close":{"reason":5},"v":2}"""));
        Assert.Equal(new SiloCastMessage.Close(null), Decode("""{"type":"close","close":"replaced","v":2}"""));
    }

    [Fact]
    public void AnUnknownType_IsUnknown_NotMalformed()
    {
        Assert.Equal(new SiloCastMessage.Unknown("queue_update"), Decode("""{"type":"queue_update","queueUpdate":{"items":[]},"v":3}"""));
        // What only a receiver sends is nothing a receiver handles.
        Assert.Equal(new SiloCastMessage.Unknown("state"), Decode("""{"type":"state","state":{"title":"x"},"v":2}"""));
        Assert.Equal(new SiloCastMessage.Unknown("handoff_challenge"), Decode("""{"type":"handoff_challenge","v":2}"""));
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        var hello = Assert.IsType<SiloCastMessage.PhoneHello>(Decode(
            """{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":[2],"clientKind":"integration","later":{"a":[1,{"b":null}]}},"later":true,"v":2}"""));
        Assert.Equal("phone-1", hello.DeviceId);
        Assert.Equal(Control("pause"), Decode("""{"type":"control","control":{"name":"pause","later":1},"v":2}"""));
    }

    public static TheoryData<string> NotEnvelopes => new()
    {
        "[]",
        "3",
        "\"ping\"",
        "not json",
        "",
        """{"v":2}""",
        """{"type":7,"v":2}""",
        """{"type":null,"v":2}""",
        // A member twice, at the top and inside a payload.
        """{"type":"ping","type":"pong","v":2}""",
        """{"type":"control","control":{"name":"seek","seconds":1,"seconds":2},"v":2}""",
        // Nesting 70 deep inside a payload nothing reads.
        $$$"""{"type":"ping","later":{{{new string('[', 70)}}}{{{new string(']', 70)}}},"v":2}""",
    };

    [Theory]
    [MemberData(nameof(NotEnvelopes))]
    public void WhatIsNotAnEnvelope_IsMalformed(string frame) => Assert.Null(Decode(frame));

    [Fact]
    public void TheReceiversHello_SaysTvAndListsTheFiveFeatures()
    {
        Assert.Equal(
            $$$"""{"type":"hello","v":2,"hello":{"role":"tv","deviceName":"Elena's PC","deviceId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","serverId":"{{{ServerId}}}","serverName":"Example Server","serverIdentity":"deploy-1","supportedVersions":[2],"features":["multi_controller","close_reason","resume_token","replace_confirmation","presence"]}}""",
            Text(SiloCastMessages.Hello(Receiver)));
        Assert.Equal(
            $$$"""{"type":"hello","v":2,"hello":{"role":"tv","deviceName":"Elena's PC","deviceId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","serverId":"{{{ServerId}}}","serverName":"Example Server","supportedVersions":[2],"features":["multi_controller","close_reason","resume_token","replace_confirmation","presence"]}}""",
            Text(SiloCastMessages.Hello(Receiver with { ServerIdentity = null })));
    }

    [Fact]
    public void AState_HasEveryFieldThePhoneRequires()
    {
        string text = Text(SiloCastMessages.State(SiloCastState.Idle));

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal("state", root.GetProperty("type").GetString());
        Assert.Equal(2, root.GetProperty("v").GetInt32());
        var state = root.GetProperty("state");
        // The keys silo-apple's testEncodedStateCarriesEveryFieldOldPeersRequire lists: Android's decoder fails without one.
        foreach (string key in (string[])["title", "isPlaying", "isLoading", "isBuffering", "currentTime", "duration", "audioTracks",
            "subtitleTracks", "qualityOptions", "activeQualityId", "isQualitySwitching", "playbackSpeed", "videoGravity",
            "hdrEnabled", "supportsVideoGravity", "supportsHDRToggle", "volume", "isMuted", "hasNextEpisode"])
        {
            Assert.True(state.TryGetProperty(key, out _), key);
        }
        Assert.Equal("Ready", state.GetProperty("title").GetString());
        Assert.False(state.GetProperty("isPlaying").GetBoolean());
        Assert.False(state.GetProperty("isLoading").GetBoolean());
        Assert.False(state.GetProperty("hasNextEpisode").GetBoolean());
        Assert.False(state.GetProperty("supportsHDRToggle").GetBoolean());
        Assert.False(state.GetProperty("supportsSubtitlePosition").GetBoolean());
        Assert.Equal(0, state.GetProperty("audioTracks").GetArrayLength());
        Assert.Equal(0, state.GetProperty("subtitleTracks").GetArrayLength());
        Assert.Equal(0, state.GetProperty("qualityOptions").GetArrayLength());
        Assert.Equal("auto", state.GetProperty("activeQualityId").GetString());
        Assert.Equal("fit", state.GetProperty("videoGravity").GetString());
        foreach (string absent in (string[])["contentId", "sessionId", "posterUrl", "overview"])
        {
            Assert.False(state.TryGetProperty(absent, out _), absent);
        }
        Assert.DoesNotContain("null", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AStateOfATitle_CarriesTracksWithIntegerIds()
    {
        var state = SiloCastState.Idle with
        {
            ContentId = "movie-1",
            Title = "Example Movie",
            IsPlaying = true,
            CurrentTime = 1798.292724609375,
            Duration = 5400,
            AudioTracks = [new SiloCastTrack("audio", 1, "English", "Dolby Digital 5.1"), new SiloCastTrack("audio", 2, "German", null)],
            SubtitleTracks = [new SiloCastTrack("subtitle", 0, "English", "SDH")],
            SelectedAudioTrackId = 1,
            SelectedSubtitleTrackId = 0,
            QualityOptions = [new SiloCastQuality("auto", "Auto", null), new SiloCastQuality("1080p", "1080p", "8 Mbps")],
            SupportsVideoGravity = true,
            SupportsSubtitleDelay = true,
            SubtitleSyncMs = -250,
            Volume = 0.4,
        };

        using var document = JsonDocument.Parse(SiloCastMessages.State(state));
        var body = document.RootElement.GetProperty("state");

        Assert.Equal("movie-1", body.GetProperty("contentId").GetString());
        Assert.Equal("Example Movie", body.GetProperty("title").GetString());
        Assert.Equal(1798.292724609375, body.GetProperty("currentTime").GetDouble());
        var audio = body.GetProperty("audioTracks");
        Assert.Equal("""{"kind":"audio","trackId":1,"title":"English","detail":"Dolby Digital 5.1"}""", audio[0].GetRawText());
        Assert.Equal("""{"kind":"audio","trackId":2,"title":"German"}""", audio[1].GetRawText());
        Assert.Equal("0", body.GetProperty("subtitleTracks")[0].GetProperty("trackId").GetRawText());
        Assert.Equal("1", body.GetProperty("selectedAudioTrackId").GetRawText());
        Assert.Equal("0", body.GetProperty("selectedSubtitleTrackId").GetRawText());
        Assert.Equal("""{"id":"1080p","label":"1080p","detail":"8 Mbps"}""", body.GetProperty("qualityOptions")[1].GetRawText());
        Assert.Equal(-250, body.GetProperty("subtitleSyncMs").GetInt32());
        Assert.Equal(0.4, body.GetProperty("volume").GetDouble());
        Assert.DoesNotContain("Example Movie", state.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AClose_CarriesItsReasonOnlyWhenGiven()
    {
        Assert.Equal("""{"type":"close","v":2}""", Text(SiloCastMessages.Close(null)));
        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"replaced"}}""", Text(SiloCastMessages.Close(SiloCastCloseReasons.Replaced)));
    }

    [Fact]
    public void ThePing_ThePong_AnError_AndACancel_HaveTheirShapes()
    {
        Assert.Equal("""{"type":"ping","v":2}""", Text(SiloCastMessages.Ping()));
        Assert.Equal("""{"type":"pong","v":2}""", Text(SiloCastMessages.Pong()));
        Assert.Equal(
            """{"type":"error","v":2,"error":{"code":"server_mismatch","message":"This PC is connected to a different Silo server."}}""",
            Text(SiloCastMessages.Error(SiloCastErrors.ServerMismatch, "This PC is connected to a different Silo server.")));
        Assert.Equal(
            """{"type":"handoff_cancel","v":2,"handoffCancel":{"requestId":"req-1","reason":"handoff_failed","message":"This PC plays from Example Server."}}""",
            Text(SiloCastMessages.HandoffCancel("req-1", SiloCastCancelReasons.HandoffFailed, "This PC plays from Example Server.")));
    }

    [Fact]
    public void HandoffReady_HasTheRequiredSessionEnd_InUtc()
    {
        var expires = new DateTimeOffset(2026, 11, 14, 20, 17, 36, 250, TimeSpan.FromHours(1));

        Assert.Equal(
            $$$"""{"type":"handoff_ready","v":2,"handoffReady":{"requestId":"req-1","serverId":"{{{ServerId}}}","profileId":"7","sessionExpiresAt":"2026-11-14T19:17:36Z","reused":true,"resumeToken":"token-1"}}""",
            Text(SiloCastMessages.HandoffReady("req-1", ServerId, "7", expires, "token-1")));
    }

    [Fact]
    public void TextsFromGranary_AreNotEscapedBeyondJson()
    {
        string hello = Text(SiloCastMessages.Hello(Receiver with { Name = "Elena's PC <Zoë> & \"more\"" }));

        Assert.Contains("\"deviceName\":\"Elena's PC <Zoë> & \\\"more\\\"\"", hello, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReceiversOwnHello_ReadsBack_AsATvsHello()
    {
        var hello = Assert.IsType<SiloCastMessage.PhoneHello>(SiloCastMessages.Decode(SiloCastMessages.Hello(Receiver)));

        Assert.Equal("tv", hello.Role);
        Assert.Equal(Receiver.ReceiverId, hello.DeviceId);
        Assert.Equal(SiloCastFeatures.Receiver.Order(StringComparer.Ordinal), hello.Features.Order(StringComparer.Ordinal));
        Assert.Equal([SiloCastMessages.Version], hello.SupportedVersions);
    }

    [Fact]
    public void AMessage_NeverPrintsWhatItCarries()
    {
        var hello = Decode(
            """{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":[2]},"v":2}""");

        Assert.NotNull(hello);
        Assert.DoesNotContain("Elena", hello.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("phone-1", hello.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AHelloWithAnOverlongDeviceId_IsMalformed()
    {
        string fits = new('a', SiloCastMessages.MaxDeviceIdLength);
        string over = new('a', SiloCastMessages.MaxDeviceIdLength + 1);

        Assert.IsType<SiloCastMessage.PhoneHello>(Decode(HelloWith(fits, """["close_reason"]""")));
        Assert.Null(Decode(HelloWith(over, """["close_reason"]""")));
    }

    [Fact]
    public void AHello_KeepsAtMostItsFirst32Features()
    {
        string features = "[" + string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"feature-{i}\"")) + "]";

        var hello = Assert.IsType<SiloCastMessage.PhoneHello>(Decode(HelloWith("phone-1", features)));

        Assert.Equal(SiloCastMessages.MaxFeatures, hello.Features.Count);
        Assert.Contains("feature-0", hello.Features);
        Assert.Contains("feature-31", hello.Features);
        Assert.DoesNotContain("feature-32", hello.Features);
    }

    [Fact]
    public void TheWireShapes_NeverPrintWhatTheyCarry()
    {
        object[] shapes =
        [
            new WireHello { DeviceName = "Elena's phone", DeviceId = "phone-1", ServerId = ServerId, ServerName = "Example Server" },
            new WireOffer { RequestId = "request-1", ServerId = ServerId, ServerUrl = "https://silo.example", ProfileId = "7", ResumeToken = "token-1" },
            new WireCancel { RequestId = "request-1", Reason = "handoff_failed", Message = "This PC plays from Example Server." },
            new WireControl { Name = "seek", Seconds = 1798.25 },
            new WireLaunch { ServerId = ServerId, Playback = new WirePlayback { ContentId = "movie-1" } },
            new WirePlayback { ContentId = "movie-1" },
            new WirePresence { Passive = true },
            new WireError("unauthorized", "Connect with a matching Silo account first."),
            new WireReady("request-1", ServerId, "7", "2026-11-14T19:17:36Z", true, "token-1"),
            new WireClose("tv_disconnected"),
        ];

        foreach (object shape in shapes)
        {
            string text = shape.ToString()!;
            Assert.Equal(shape.GetType().Name, text);
        }
    }

    private static SiloCastMessage.Control Control(string name) => new(name, null, null, null, null, null, null, null);

    private static string HelloWith(string deviceId, string features) =>
        $$"""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"{{deviceId}}","serverId":"{{ServerId}}","supportedVersions":[2],"features":{{features}}},"v":2}""";

    private static SiloCastMessage? Decode(string frame) => SiloCastMessages.Decode(Encoding.UTF8.GetBytes(frame));

    private static string Text(byte[] utf8) => Encoding.UTF8.GetString(utf8);
}
