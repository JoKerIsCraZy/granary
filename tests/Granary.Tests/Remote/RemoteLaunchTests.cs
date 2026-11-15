using System.Net;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Remote;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using Granary.Tests.WatchParty;

namespace Granary.Tests.Remote;

/// <summary>
/// A phone starts a title on the PC (design 5.7): the offer for the profile signed in on the PC is ready at once on the
/// PC's own sign-in, and a launch after it opens the player at the phone's position. Phones are joined in memory
/// (<see cref="RemoteHarness"/>), the player route is recorded, and every deadline runs on a <see cref="ManualTimeProvider"/>.
/// </summary>
public sealed class RemoteLaunchTests : IAsyncDisposable
{
    private const string AtThePhonesPosition = """{"contentId":"movie-1","startFromBeginning":false,"resumePosition":4747.284}""";

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly RemoteHarness _harness = new();
    private WatchPartySession? _party;

    // Profile 7 on https://silo.example, the server the phones say hello on.
    public RemoteLaunchTests() => _harness.SignIn();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private RemoteReceiver Receiver => _harness.Receiver;

    private IReadOnlyList<(AppRoute Route, object? Parameter)> Navigations => _harness.Navigation.Navigations;

    public async ValueTask DisposeAsync()
    {
        if (_party is not null)
        {
            await _party.LeaveAsync();
        }
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task AnOfferForTheSignedInProfile_IsReadyAtOnce_OnThePcsOwnSignIn()
    {
        var phone = await PhoneAsync();

        await phone.SendAsync(Offer());

        using var ready = JsonDocument.Parse(await phone.NextAsync("handoff_ready"));
        var payload = ready.RootElement.GetProperty("handoffReady");
        Assert.Equal("request-1", payload.GetProperty("requestId").GetString());
        Assert.Equal(RemoteHarness.ServerId, payload.GetProperty("serverId").GetString());
        Assert.Equal("7", payload.GetProperty("profileId").GetString());
        Assert.True(payload.GetProperty("reused").GetBoolean());
        // A day on from the PC's clock (2026-10-01 12:00 UTC); nothing reads it, but the phones require it.
        Assert.Equal("2026-10-02T12:00:00Z", payload.GetProperty("sessionExpiresAt").GetString());
        Assert.Matches("^[0-9a-f]{64}$", payload.GetProperty("resumeToken").GetString());
        // Nothing was asked of the session: no sign-in, no profile, no server.
        Assert.Empty(_harness.Session.Calls);
    }

    [Fact]
    public async Task AnOfferWhoseServerIdDiffersOnlyInCase_GetsItsOwnSpellingBack()
    {
        // The phone's spelling of the address, in capitals: base64url of it as the phone has it, which Uri would lower-case.
        string phoneSpelling = Convert.ToBase64String("HTTPS://SILO.EXAMPLE"u8).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var phone = await PhoneAsync();

        await phone.SendAsync(Offer(serverId: phoneSpelling));

        Assert.Contains($"\"serverId\":\"{phoneSpelling}\"", await phone.NextAsync("handoff_ready"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOfferForAnotherProfile_IsCancelled_WithTheReasonForThePhone()
    {
        var phone = await PhoneAsync();

        await phone.SendAsync(Offer(profileId: "8"));

        Assert.Equal("""{"type":"handoff_cancel","v":2,"handoffCancel":{"requestId":"request-1","reason":"handoff_failed","message":"Granary is signed in with another profile. Switch profiles on the PC to play from this phone."}}""",
            await phone.NextAsync("handoff_cancel"));
    }

    [Fact]
    public async Task AnOfferForAnotherServer_IsCancelled_NamingThePcsServer()
    {
        var phone = await PhoneAsync();

        await phone.SendAsync(Offer(serverId: SiloCastServerId.From(new Uri("https://other.example"))));

        Assert.Equal("""{"type":"handoff_cancel","v":2,"handoffCancel":{"requestId":"request-1","reason":"handoff_failed","message":"This PC plays from Example Server."}}""",
            await phone.NextAsync("handoff_cancel"));
    }

    [Fact]
    public async Task AnOfferFromAPhoneOnAnotherServer_IsCancelled_EvenNamingThePcsServer()
    {
        // Its hello named another server: an offer that names Granary's does not make it one of this server's phones.
        var (phone, _) = await _harness.HelloAsync(SiloCastServerId.From(new Uri("https://other.example")));

        await phone.SendAsync(Offer());

        Assert.Contains("This PC plays from Example Server.", await phone.NextAsync("handoff_cancel"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOfferFromAPhoneThatSaidNoHello_IsCancelledAsVersionUnsupported()
    {
        var (phone, _, _) = await _harness.ServeAsync();

        await phone.SendAsync(Offer());

        Assert.Equal("""{"type":"handoff_cancel","v":2,"handoffCancel":{"requestId":"request-1","reason":"version_unsupported","message":"Update Silo on both devices to continue."}}""",
            await phone.NextAsync("handoff_cancel"));
    }

    [Fact]
    public async Task AResumeTokenSentBack_ChangesNothing()
    {
        var phone = await PhoneAsync();
        await phone.SendAsync(Offer());
        using var ready = JsonDocument.Parse(await phone.NextAsync("handoff_ready"));
        string token = ready.RootElement.GetProperty("handoffReady").GetProperty("resumeToken").GetString()!;

        await phone.SendAsync(Offer("request-2", profileId: "8", extra: $$""","resumeToken":"{{token}}" """));

        string cancel = await phone.NextAsync("handoff_cancel");
        Assert.Contains("\"requestId\":\"request-2\"", cancel, StringComparison.Ordinal);
        Assert.Contains("another profile", cancel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReceiver_NeverAsksForReplaceConfirmation()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher) { State = FakeRemotePlayer.Playing with { ContentId = "episode-1" } };
        await _harness.AttachAsync(player);
        var phone = await PhoneAsync();

        // A title plays, and the offer carries no replaceConfirmed: it is ready all the same.
        await phone.SendAsync(Offer());

        Assert.Contains("request-1", await phone.NextAsync("handoff_ready"), StringComparison.Ordinal);
        Assert.DoesNotContain(phone.Received, message => message.Contains("replace_confirmation_required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALaunchAfterReady_OpensThePlayerAtThePhonesPosition()
    {
        var phone = await ReadyAsync();

        await phone.SendAsync(Launch(AtThePhonesPosition));

        await LoadingAsync(phone);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.At("movie-1", 4747.284)), Assert.Single(Navigations));
    }

    [Fact]
    public async Task StartFromBeginning_StartsOver()
    {
        Assert.Equal(PlayRequest.FromStart("movie-1"),
            await LaunchedAsync("""{"contentId":"movie-1","startFromBeginning":true,"resumePosition":4747.284}"""));
    }

    [Theory]
    [InlineData("""{"contentId":"movie-1","startFromBeginning":false}""")]
    [InlineData("""{"contentId":"movie-1","startFromBeginning":false,"resumePosition":0}""")]
    [InlineData("""{"contentId":"movie-1","startFromBeginning":false,"resumePosition":-3}""")]
    public async Task NeitherAPositionNorFromTheBeginning_Resumes(string playback)
    {
        Assert.Equal(PlayRequest.Resume("movie-1"), await LaunchedAsync(playback));
    }

    [Fact]
    public async Task AVersionAndTracks_BecomeThePrePlayChoice()
    {
        var request = await LaunchedAsync(
            """{"contentId":"movie-1","fileId":42,"pinsVersion":true,"audioTrackIndex":1,"subtitleTrackIndex":3,"libraryId":5,"startFromBeginning":false,"resumePosition":4747.284}""");

        Assert.Equal(PlayRequest.At("movie-1", 4747.284, new PrePlayChoice("42", IsVersionPicked: true, AudioTrack: 1, SubtitleTrack: 3)), request);
    }

    [Fact]
    public async Task ANegativeSubtitleIndex_MeansOff()
    {
        var request = await LaunchedAsync(
            """{"contentId":"movie-1","fileId":42,"audioTrackIndex":-2,"subtitleTrackIndex":-4,"startFromBeginning":false}""");

        // A negative audio pick is none, as Silo's Roku app reads it: the player chooses.
        Assert.Equal(new PrePlayChoice("42", IsVersionPicked: false, AudioTrack: null, SubtitleTrack: -1), request.Choice);
    }

    [Fact]
    public async Task TrackPicksWithoutAFile_AreDropped()
    {
        var request = await LaunchedAsync("""{"contentId":"movie-1","audioTrackIndex":1,"subtitleTrackIndex":3,"startFromBeginning":true}""");

        Assert.Null(request.Choice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    public async Task ALaunchThatNamesNoTitle_OpensNothing(string contentId)
    {
        var phone = await ReadyAsync();

        await phone.SendAsync(Launch($$"""{"contentId":"{{contentId}}","startFromBeginning":true}"""));

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"launch_failed","message":"This title is no longer available."}}""",
            await phone.NextAsync("error"));
        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task ALaunchWithoutAReadyOffer_IsRefused()
    {
        var phone = await PhoneAsync();

        await phone.SendAsync(Launch(AtThePhonesPosition));

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"handoff_required","message":"Prepare the phone profile before playing."}}""",
            await phone.NextAsync("error"));
        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task AReadyOfferOnAnotherConnection_DoesNotCount()
    {
        await ReadyAsync();
        var other = await PhoneAsync(deviceId: "phone-2");

        await other.SendAsync(Launch(AtThePhonesPosition));

        Assert.Contains("handoff_required", await other.NextAsync("error"), StringComparison.Ordinal);
        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task ANewerReadyOffer_IsTheOnlyOne()
    {
        // As Silo's receivers have it, readiness is one phone's alone: the newest offer's.
        var first = await ReadyAsync();
        await ReadyAsync(deviceId: "phone-2");

        await first.SendAsync(Launch(AtThePhonesPosition));

        Assert.Contains("handoff_required", await first.NextAsync("error"), StringComparison.Ordinal);
        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task AnOfferFromAConnectionOnItsWayOut_ReadiesNothing()
    {
        var old = await PhoneAsync();
        FakePhone? newer = null;

        // The old connection's offer waits for the UI thread while a newer connection of the same phone takes its place.
        await WhileTheUiThreadIsBusyAsync(async () =>
        {
            await old.SendAsync(Offer());
            await _harness.Dispatcher.UntilWaitingAsync(1, Token);
            newer = await PhoneAsync();
        });
        await newer!.SendAsync(Offer("request-2"));
        await newer.NextAsync("handoff_ready");
        await newer.SendAsync(Launch(AtThePhonesPosition));

        await LoadingAsync(newer);
        Assert.DoesNotContain("Connection 1: an offer to start a title is ready", _harness.Logs.All, StringComparison.Ordinal);
        Assert.Single(Navigations);
    }

    [Fact]
    public async Task AfterTheProfileChanged_TheOldOfferIsVoid()
    {
        var phone = await ReadyAsync();

        // Another profile, then the same one again: the offer was made for a sign-in that is gone.
        _harness.SignIn(profileId: "8");
        _harness.SignIn(profileId: "7");
        await phone.SendAsync(Launch(AtThePhonesPosition));

        Assert.Contains("handoff_required", await phone.NextAsync("error"), StringComparison.Ordinal);
        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task AfterAnotherServer_TheOldOfferIsVoid()
    {
        var phone = await ReadyAsync();

        _harness.SignIn(RemoteHarness.Server("https://other.example", "entry-2"));
        _harness.SignIn();
        await phone.SendAsync(Launch(AtThePhonesPosition));

        Assert.Contains("handoff_required", await phone.NextAsync("error"), StringComparison.Ordinal);
        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task AnotherChangeOfTheSession_KeepsTheOffer()
    {
        // The session says it changed (a token refreshed, the account read again) for the same server and profile.
        var phone = await ReadyAsync();

        _harness.Session.RaiseChanged();
        await phone.SendAsync(Launch(AtThePhonesPosition));

        await LoadingAsync(phone);
        Assert.Single(Navigations);
    }

    [Fact]
    public async Task ALaunchForAnotherServer_IsRefused()
    {
        var phone = await ReadyAsync();

        await phone.SendAsync(Launch(AtThePhonesPosition, SiloCastServerId.From(new Uri("https://other.example"))));

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"server_mismatch","message":"This PC is connected to a different Silo server."}}""",
            await phone.NextAsync("error"));
        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task AnUnauthorizedPhonesLaunch_IsRefused()
    {
        var (phone, _) = await _harness.HelloAsync(SiloCastServerId.From(new Uri("https://other.example")));

        await phone.SendAsync(Launch(AtThePhonesPosition));

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"unauthorized","message":"Connect with a matching Silo account first."}}""",
            await phone.NextAsync("error"));
        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task ASecondLaunchWhileTheFirstStillOpens_IsPending()
    {
        var phone = await ReadyAsync();
        await phone.SendAsync(Launch(AtThePhonesPosition));
        await LoadingAsync(phone);

        await phone.SendAsync(Launch("""{"contentId":"movie-2","startFromBeginning":true}"""));

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"launch_pending","message":"A title is already starting on this PC."}}""",
            await phone.NextAsync("error"));
        Assert.Single(Navigations);

        // Half a minute on, the first no longer counts as opening; the phone answers its pings meanwhile.
        for (var waited = TimeSpan.Zero; waited < RemoteReceiver.LaunchWait; waited += TimeSpan.FromSeconds(3))
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(3));
            await phone.NextAsync("ping");
        }
        await _harness.SettleUiAsync();
        await phone.SendAsync(Launch("""{"contentId":"movie-2","startFromBeginning":true}"""));
        await LoadingAsync(phone, "movie-2");
        Assert.Equal(2, Navigations.Count);
    }

    [Fact]
    public async Task TheLaunchedTitle_ShowsInTheStatusAtOnce_AsLoading()
    {
        var phone = await ReadyAsync();

        await phone.SendAsync(Launch(AtThePhonesPosition));

        // Before any player opened, let alone a stream: the phone gives up after 30 seconds without its title.
        using var state = JsonDocument.Parse(await LoadingAsync(phone));
        var payload = state.RootElement.GetProperty("state");
        Assert.Equal("Loading", payload.GetProperty("title").GetString());
        Assert.False(payload.GetProperty("isPlaying").GetBoolean());
    }

    [Fact]
    public async Task OnceTheLaunchedTitlePlays_AnotherLaunchMayFollow()
    {
        var phone = await ReadyAsync();
        await phone.SendAsync(Launch(AtThePhonesPosition));
        await LoadingAsync(phone);
        var player = new FakeRemotePlayer(_harness.Dispatcher)
        {
            State = SiloCastState.Loading("movie-1"),
        };
        await _harness.AttachAsync(player);
        await PlayerChangedAsync(player, FakeRemotePlayer.Playing);

        await phone.SendAsync(Launch("""{"contentId":"movie-2","startFromBeginning":true}"""));

        await LoadingAsync(phone, "movie-2");
        Assert.Equal(2, Navigations.Count);
    }

    [Fact]
    public async Task ATitleThatFailsToStart_SendsLaunchFailed_WithThePlayersMessage()
    {
        var phone = await ReadyAsync();
        await phone.SendAsync(Launch(AtThePhonesPosition));
        await LoadingAsync(phone);
        var player = new FakeRemotePlayer(_harness.Dispatcher) { State = SiloCastState.Loading("movie-1") };
        await _harness.AttachAsync(player);

        await PlayerChangedAsync(player, SiloCastState.Loading("movie-1") with
        {
            Title = "Example Movie",
            IsLoading = false,
            Error = "This title is no longer available.",
        });

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"launch_failed","message":"This title is no longer available."}}""",
            await phone.NextAsync("error"));
        // It is no longer opening: the phone may try another.
        await phone.SendAsync(Launch("""{"contentId":"movie-2","startFromBeginning":true}"""));
        await LoadingAsync(phone, "movie-2");
    }

    [Fact]
    public async Task ALaunchOfATitleThatFailedHere_WaitsForItsOwnPlayer()
    {
        // The PC's player shows the same title failed; the launch replaces it, and the old failure is not the launch's.
        var failed = new FakeRemotePlayer(_harness.Dispatcher)
        {
            State = SiloCastState.Loading("movie-1") with { IsLoading = false, Error = "This title is no longer available." },
        };
        await _harness.AttachAsync(failed);
        var phone = await ReadyAsync();

        await phone.SendAsync(Launch(AtThePhonesPosition));
        await LoadingAsync(phone);
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");

        Assert.DoesNotContain(phone.Received, message => message.Contains("launch_failed", StringComparison.Ordinal));
        await _harness.DetachAsync(failed);
        await _harness.AttachAsync(new FakeRemotePlayer(_harness.Dispatcher));
        await StateWithAsync(phone, "\"title\":\"Example Movie\"");
    }

    [Fact]
    public async Task ALaunchedTitleWhosePlayerClosesWhileItLoads_IsNoLongerOpening()
    {
        var phone = await ReadyAsync();
        await phone.SendAsync(Launch(AtThePhonesPosition));
        await LoadingAsync(phone);
        var player = new FakeRemotePlayer(_harness.Dispatcher) { State = SiloCastState.Loading("movie-1") };
        await _harness.AttachAsync(player);

        // The viewer closed the player before the title loaded: the phone sees the PC idle again.
        await _harness.DetachAsync(player);

        await StateWithAsync(phone, "\"title\":\"Ready\"");
        await phone.SendAsync(Launch("""{"contentId":"movie-2","startFromBeginning":true}"""));
        await LoadingAsync(phone, "movie-2");
    }

    [Fact]
    public async Task ALaunchWhileATitlePlays_ReplacesIt()
    {
        var playing = new FakeRemotePlayer(_harness.Dispatcher) { State = FakeRemotePlayer.Playing with { ContentId = "episode-1" } };
        await _harness.AttachAsync(playing);
        var phone = await ReadyAsync();

        await phone.SendAsync(Launch(AtThePhonesPosition));

        // Nobody is asked: the player opens the phone's title, and the phone sees it at once, not the title before.
        await LoadingAsync(phone);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.At("movie-1", 4747.284)), Assert.Single(Navigations));
        // The page that played it goes, the new one comes.
        await _harness.DetachAsync(playing);
        await _harness.AttachAsync(new FakeRemotePlayer(_harness.Dispatcher));
        await StateWithAsync(phone, "\"title\":\"Example Movie\"");
    }

    [Fact]
    public async Task DuringAWatchParty_OffersAreCancelled_AndLaunchesRefused()
    {
        var party = WatchParty();
        var phone = await ReadyAsync();

        // This profile goes into a room, as the room page does; the receiver learns of it from the Watch Party alone.
        await party.JoinAsync("ABCD1234", Token);
        Assert.True(party.IsInRoom);

        await phone.SendAsync(Launch(AtThePhonesPosition));
        Assert.Equal("""{"type":"error","v":2,"error":{"code":"watch_party_active","message":"Leave the Watch Party on the PC first."}}""",
            await phone.NextAsync("error"));
        await phone.SendAsync(Offer("request-2"));
        Assert.Equal("""{"type":"handoff_cancel","v":2,"handoffCancel":{"requestId":"request-2","reason":"watch_party_active","message":"Leave the Watch Party on the PC first."}}""",
            await phone.NextAsync("handoff_cancel"));

        Assert.Empty(Navigations);
    }

    [Fact]
    public async Task WhileThePcIsLocked_ALaunchIsRefused_ButControlStillWorks()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        await _harness.AttachAsync(player);
        var phone = await ReadyAsync();
        _harness.Lock.IsLocked = true;

        await phone.SendAsync(Launch(AtThePhonesPosition));
        Assert.Equal("""{"type":"error","v":2,"error":{"code":"player_not_ready","message":"The PC player is not ready yet."}}""",
            await phone.NextAsync("error"));
        await phone.SendAsync("""{"type":"control","control":{"name":"pause"},"v":2}""");
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");
        await _harness.SettleUiAsync();

        Assert.Empty(Navigations);
        Assert.Equal(SiloCastControls.Pause, Assert.Single(player.Applied).Name);
    }

    [Fact]
    public async Task NothingAPhoneNames_IsEverContacted()
    {
        var phone = await PhoneAsync();

        // An offer that names an address of its own, other endpoints and a token of its own.
        await phone.SendAsync("""{"type":"handoff_offer","handoffOffer":{"requestId":"request-1","serverId":"__SERVER__","serverURL":"https://evil.example","serverEndpoints":[{"url":"https://evil.example/a"},{"url":"http://192.168.1.66:8080"}],"serverName":"Evil","profileId":"7","profileName":"Elena","title":"Example Movie","resumeToken":"phone-token"},"v":2}"""
            .Replace("__SERVER__", RemoteHarness.ServerId, StringComparison.Ordinal));
        await phone.NextAsync("handoff_ready");
        await phone.SendAsync(Launch(AtThePhonesPosition));
        await LoadingAsync(phone);

        // The player opens the title on the PC's own sign-in: its request names the title and the position, nothing else.
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.At("movie-1", 4747.284)), Assert.Single(Navigations));
        Assert.Empty(_harness.Session.Calls);
        Assert.DoesNotContain(phone.Received, message => message.Contains("evil", StringComparison.OrdinalIgnoreCase)
            || message.Contains("phone-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALaunch_RaisesLaunched_WithThePhonesCleanedName()
    {
        var launched = new List<(RemoteLaunch Launch, bool OnUiThread, int Navigated)>();
        // Raised on the UI thread before the player opens, so the window can say who started the title it is about to show.
        Receiver.Launched += (_, launch) => launched.Add((launch, _harness.Dispatcher.HasThreadAccess, Navigations.Count));
        var phone = await ReadyAsync(deviceName: "Elena's​ phone\u0007");

        await phone.SendAsync(Launch(AtThePhonesPosition));
        await LoadingAsync(phone);
        await _harness.SettleUiAsync();

        var (only, onUiThread, navigated) = Assert.Single(launched);
        Assert.Equal("Elena's phone", only.PhoneName);
        Assert.True(onUiThread);
        Assert.Equal(0, navigated);
        Assert.Same(Assert.Single(Navigations).Parameter, only.Request);
        Assert.Equal("RemoteLaunch", only.ToString());
    }

    [Fact]
    public async Task TheSessionTheRealPhoneRan_RunsToItsEnd()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher) { State = FakeRemotePlayer.Playing with { ContentId = "episode-1" } };
        player.Answer = command =>
        {
            if (command is { Name: SiloCastControls.Seek, Seconds: { } seconds })
            {
                player.State = player.State with { CurrentTime = seconds };
            }
            return RemoteCommandResult.Done;
        };
        await _harness.AttachAsync(player);

        // The hello the real phone sent, with sample values; then its offer to replace what plays, and its launch.
        var (phone, serving) = await _harness.HelloAsync(serverIdentity: RemoteHarness.ServerIdentity);
        Assert.Contains("episode-1", await phone.NextAsync("state"), StringComparison.Ordinal);
        await phone.SendAsync(Offer(extra: ""","replaceConfirmed":true,"replacingContentId":"episode-1" """));
        Assert.Contains("request-1", await phone.NextAsync("handoff_ready"), StringComparison.Ordinal);
        await phone.SendAsync(Launch(AtThePhonesPosition));
        await LoadingAsync(phone);
        Assert.Equal((AppRoute.Player, (object?)PlayRequest.At("movie-1", 4747.284)), Assert.Single(Navigations));

        // The page that played the episode goes; the new one plays the phone's title, which the phone then controls.
        await _harness.DetachAsync(player);
        player.State = FakeRemotePlayer.Playing with { CurrentTime = 4747.284 };
        await _harness.AttachAsync(player);
        await StateWithAsync(phone, "\"title\":\"Example Movie\"");
        for (int beat = 0; beat < 3; beat++)
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(3));
            await phone.NextAsync("ping");
        }
        await phone.SendAsync("""{"type":"control","control":{"name":"seek","seconds":1798.292724609375},"v":2}""");
        while (!(await phone.NextAsync("state")).Contains("\"currentTime\":1798.292724609375", StringComparison.Ordinal))
        {
        }
        await phone.SendAsync("""{"type":"close","v":2}""");
        await serving.WaitAsync(HangGuard, Token);

        Assert.Equal(SiloCastControls.Seek, Assert.Single(player.Applied).Name);
        Assert.Equal(0, phone.OpenFailures);
    }

    private static string Offer(string requestId = "request-1", string? serverId = null, string profileId = "7", string extra = "") =>
        $$"""{"type":"handoff_offer","handoffOffer":{"requestId":"{{requestId}}","serverId":"{{serverId ?? RemoteHarness.ServerId}}","serverURL":"https://silo.example","profileId":"{{profileId}}","profileName":"Elena"{{extra.Trim()}}},"v":2}""";

    private static string Launch(string playback, string? serverId = null) =>
        $$"""{"type":"launch","launch":{"serverId":"{{serverId ?? RemoteHarness.ServerId}}","playback":{{playback}}},"v":2}""";

    // A phone on Granary's server that said hello and read its first status.
    private async Task<FakePhone> PhoneAsync(string deviceId = FakePhone.DefaultDeviceId, string deviceName = FakePhone.DefaultDeviceName)
    {
        var (phone, _) = await _harness.HelloAsync(deviceId: deviceId, deviceName: deviceName);
        await phone.NextAsync("state");
        return phone;
    }

    // Such a phone whose offer for profile 7 was answered ready.
    private async Task<FakePhone> ReadyAsync(string deviceId = FakePhone.DefaultDeviceId, string deviceName = FakePhone.DefaultDeviceName)
    {
        var phone = await PhoneAsync(deviceId, deviceName);
        await phone.SendAsync(Offer());
        await phone.NextAsync("handoff_ready");
        return phone;
    }

    // The request a ready phone's launch of <paramref name="playback"/> opened the player with.
    private async Task<PlayRequest> LaunchedAsync(string playback)
    {
        var phone = await ReadyAsync();
        await phone.SendAsync(Launch(playback));
        await LoadingAsync(phone);
        return Assert.IsType<PlayRequest>(Assert.Single(Navigations).Parameter);
    }

    // The next status that shows <paramref name="contentId"/> loading; the navigation came before it.
    private static async Task<string> LoadingAsync(FakePhone phone, string contentId = "movie-1")
    {
        while (true)
        {
            string state = await phone.NextAsync("state");
            if (state.Contains($"\"contentId\":\"{contentId}\"", StringComparison.Ordinal) && state.Contains("\"isLoading\":true", StringComparison.Ordinal))
            {
                return state;
            }
        }
    }

    // The player's status changes to <paramref name="state"/>, as a player reports it, on the UI thread.
    private Task PlayerChangedAsync(FakeRemotePlayer player, SiloCastState state) => _harness.Dispatcher.InvokeAsync(() =>
    {
        player.State = state;
        player.RaiseStateChanged();
        return true;
    });

    // The Watch Party of the receiver, made before the receiver, in no room yet: joining one goes through its API.
    private WatchPartySession WatchParty()
    {
        var silo = new ScriptedSilo();
        silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room())));
        silo.On(HttpMethod.Post, "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId + "/ws-ticket",
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        var api = new WatchPartyApi(silo.Http);
        var session = new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
        _party = new WatchPartySession(session, api, () => new WatchPartyConnection(api, () => new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol },
            _harness.Time, _harness.Logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _harness.Logs.For<WatchPartySession>(), time: _harness.Time);
        _harness.WatchParty = _party;
        return _party;
    }

    // Runs <paramref name="work"/> while the UI thread is busy with something else, then lets it go on.
    private async Task WhileTheUiThreadIsBusyAsync(Func<Task> work)
    {
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Dispatcher.Post(() =>
        {
            started.SetResult();
            busy.Task.Wait(HangGuard, CancellationToken.None);
        });
        await started.Task.WaitAsync(HangGuard, Token);
        try
        {
            await work();
        }
        finally
        {
            busy.SetResult();
        }
    }

    // The next status that holds <paramref name="text"/>; older ones may still wait before it.
    private static async Task StateWithAsync(FakePhone phone, string text)
    {
        while (!(await phone.NextAsync("state")).Contains(text, StringComparison.Ordinal))
        {
        }
    }
}
