using System.Net;
using System.Text.Json;
using Granary.Core.Remote;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// The receiver: who may connect, who is authorized, what a phone may do, and the status it gets, with phones joined in
/// memory (<see cref="RemoteHarness"/>) and every deadline on a <see cref="ManualTimeProvider"/>.
/// </summary>
public sealed class RemoteReceiverTests : IAsyncDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static readonly IPAddress OnePeer = IPAddress.Parse("192.168.1.30");

    private readonly RemoteHarness _harness = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private RemoteReceiver Receiver => _harness.Receiver;

    private ManualTimeProvider Time => _harness.Time;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task TheReceiversHello_GoesFirst_AsSoonAsTheSessionIsKeyed()
    {
        var (phone, _, _) = await _harness.ServeAsync();

        using var hello = JsonDocument.Parse(await phone.ReadAsync() ?? "");

        var root = hello.RootElement;
        Assert.Equal("hello", root.GetProperty("type").GetString());
        var payload = root.GetProperty("hello");
        Assert.Equal("tv", payload.GetProperty("role").GetString());
        Assert.Equal("Elena's PC", payload.GetProperty("deviceName").GetString());
        Assert.Equal(RemoteHarness.ReceiverId, payload.GetProperty("deviceId").GetString());
        Assert.Equal(RemoteHarness.ServerId, payload.GetProperty("serverId").GetString());
        Assert.Equal(RemoteHarness.ServerIdentity, payload.GetProperty("serverIdentity").GetString());
        Assert.Equal(5, payload.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task APhoneOnTheSameServer_IsAuthorized_AndGetsTheStatus()
    {
        var (phone, _) = await _harness.HelloAsync();

        string state = await phone.NextAsync("state");

        Assert.Contains("\"title\":\"Ready\"", state, StringComparison.Ordinal);
        var shown = Assert.Single(Receiver.Phones);
        Assert.Equal(new ConnectedPhone(1, "Elena's phone", IsActive: true), shown);
    }

    [Fact]
    public async Task APhoneAtAnotherAddressOfTheSameServer_IsAuthorizedByItsIdentity()
    {
        string otherAddress = SiloCastServerId.From(new Uri("http://192.168.1.10:8090"));

        var (phone, _) = await _harness.HelloAsync(otherAddress, RemoteHarness.ServerIdentity);

        Assert.Contains("\"title\":\"Ready\"", await phone.NextAsync("state"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APhoneOnAnotherServer_StaysConnected_WithoutStatusOrControl()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        await _harness.AttachAsync(player);
        string otherServer = SiloCastServerId.From(new Uri("https://other.example"));
        var (phone, _) = await _harness.HelloAsync(otherServer);

        await phone.SendAsync("""{"type":"control","control":{"name":"pause"},"v":2}""");
        Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"unauthorized","message":"Connect with a matching Silo account first."}}""",
            await phone.NextAsync("error"));
        await phone.SendAsync("""{"type":"ping","v":2}""");
        Assert.Equal("pong", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        Assert.DoesNotContain(phone.Received, message => FakePhone.TypeOf(message) == "state");
        Assert.Empty(player.Applied);
        Assert.False(Assert.Single(Receiver.Phones).IsActive);
    }

    [Theory]
    [InlineData("tv", "[2]", true)]
    [InlineData("phone", "[1]", true)]
    [InlineData("phone", "[2]", false)]
    public async Task AHelloThatIsNotAPhones_GetsVersionUnsupported_AndIsClosedUnauthorized(string role, string versions, bool withServer)
    {
        var (phone, _, serving) = await _harness.ServeAsync();
        Assert.Equal("hello", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        string server = withServer ? $",\"serverId\":\"{RemoteHarness.ServerId}\"" : "";

        await phone.SendAsync($$$"""{"type":"hello","hello":{"role":"{{{role}}}","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":{{{versions}}},"features":["close_reason"]{{{server}}}},"v":2}""");

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"version_unsupported","message":"Update Silo on both devices to continue."}}""",
            await phone.ReadAsync());
        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"unauthorized"}}""", await phone.ReadAsync());
        Assert.Null(await phone.ReadAsync());
        await serving.WaitAsync(HangGuard, Token);
    }

    [Fact]
    public async Task NoStatus_GoesOutBeforeAuthorization()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        await _harness.AttachAsync(player);
        var (phone, _, _) = await _harness.ServeAsync();

        // Ticks and a change in the player while the phone has said nothing.
        Time.Advance(TimeSpan.FromSeconds(1));
        await _harness.Dispatcher.InvokeAsync(() =>
        {
            player.RaiseStateChanged();
            return true;
        });
        await _harness.SettleUiAsync();
        await phone.SendAsync("""{"type":"ping","v":2}""");
        Assert.Equal("hello", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        Assert.Equal("pong", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));

        await phone.HelloAsync(RemoteHarness.ServerId, null);

        Assert.Contains("Example Movie", await phone.ReadAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoHelloWithin5Seconds_ClosesWithTimeout()
    {
        var (phone, _, serving) = await _harness.ServeAsync();

        Time.Advance(TimeSpan.FromSeconds(5));

        await serving.WaitAsync(HangGuard, Token);
        Assert.Equal("""{"type":"close","v":2}""", await phone.NextAsync("close"));
    }

    [Fact]
    public async Task ANewerConnectionOfTheSamePhone_ClosesTheOlderAsReplaced()
    {
        var (older, olderServing) = await _harness.HelloAsync();
        Assert.Equal("state", FakePhone.TypeOf(await older.NextAsync("state")));

        var (newer, _) = await _harness.HelloAsync();

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"replaced"}}""", await older.NextAsync("close"));
        await olderServing.WaitAsync(HangGuard, Token);
        Assert.Equal("state", FakePhone.TypeOf(await newer.NextAsync("state")));
        Assert.Equal(2, Assert.Single(Receiver.Phones).Number);
    }

    [Fact]
    public async Task ThePeersAddress_MustBeLocal()
    {
        var pipe = new DuplexPipe();
        var phone = new FakePhone(pipe.Phone, RemoteHarness.Key);
        var connection = pipe.Connection(IPAddress.Parse("8.8.8.8"));
        await phone.SendSealedHelloAsync();

        await Receiver.ServeAsync(connection, Token).WaitAsync(HangGuard, Token);

        Assert.True(connection.IsDisposed);
        Assert.Null(await phone.ReadPlainAsync());
        Assert.Contains("refused: not a local address", _harness.Logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("8.8.8.8", _harness.Logs.All, StringComparison.Ordinal);
        pipe.Dispose();
    }

    [Fact]
    public async Task TheTwentyFirstConnectionInAMinuteFromOneAddress_IsClosedUnread_AndTheNextMinuteIsOpenAgain()
    {
        for (int i = 0; i < RemoteReceiverPolicy.MaxNewConnectionsPerMinute; i++)
        {
            var (phone, _, serving) = await _harness.ServeAsync(pair: false, peer: OnePeer);
            await phone.DisposeAsync();
            await serving.WaitAsync(HangGuard, Token);
            Time.Advance(TimeSpan.FromSeconds(1));
        }

        var (refused, _, refusedServing) = await _harness.ServeAsync(pair: false, peer: OnePeer);
        await refusedServing.WaitAsync(HangGuard, Token);
        Assert.Null(await refused.ReadPlainAsync());
        // From another address it goes on.
        var (other, _, _) = await _harness.ServeAsync(pair: false, peer: IPAddress.Parse("192.168.1.31"));
        Assert.True(await other.PairAsync());

        // The first of the twenty is a minute old one second after the last came.
        Time.Advance(TimeSpan.FromSeconds(41));
        var (again, _, _) = await _harness.ServeAsync(pair: false, peer: OnePeer);
        Assert.True(await again.PairAsync());
    }

    [Fact]
    public async Task AFifthConnectionWithoutAHello_ClosesTheOldestSuch_NotTheNewcomer()
    {
        var waiting = new List<Task>();
        for (int i = 0; i < RemoteReceiverPolicy.MaxUnauthenticated; i++)
        {
            waiting.Add((await _harness.ServeAsync(pair: false)).Serving);
        }

        var (fifth, _, _) = await _harness.ServeAsync(pair: false);

        // The oldest goes, unanswered; the newcomer is let in.
        await waiting[0].WaitAsync(HangGuard, Token);
        Assert.True(await fifth.PairAsync());
        Assert.All(waiting.Skip(1), serving => Assert.False(serving.IsCompleted));
        Assert.Contains("Connection 1 closed: made room for a new connection", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneAddress_HoldsAtMostTwoConnectionsWithoutAHello()
    {
        var first = await _harness.ServeAsync(pair: false, peer: OnePeer);
        var second = await _harness.ServeAsync(pair: false, peer: OnePeer);

        var third = await _harness.ServeAsync(pair: false, peer: OnePeer);

        await first.Serving.WaitAsync(HangGuard, Token);
        Assert.Null(await first.Phone.ReadPlainAsync());
        Assert.False(second.Serving.IsCompleted);
        Assert.True(await third.Phone.PairAsync());
    }

    [Fact]
    public async Task APeerThatOnlyNamedTheKey_EvictsNoPhone()
    {
        var phones = new List<(FakePhone Phone, Task Serving)>();
        var (passive, passiveServing) = await _harness.HelloAsync(deviceId: "phone-1", passive: true);
        await passive.NextAsync("state");
        phones.Add((passive, passiveServing));
        for (int i = 2; i <= 4; i++)
        {
            var (phone, serving) = await _harness.HelloAsync(deviceId: $"phone-{i}");
            await phone.NextAsync("state");
            phones.Add((phone, serving));
        }

        // The key id is public: a peer that names it and says nothing that opens has proven nothing.
        var (newcomer, _, _) = await _harness.ServeAsync();
        Assert.Equal("hello", FakePhone.TypeOf(await newcomer.ReadAsync() ?? ""));

        Assert.Equal(4, Receiver.Phones.Count);
        foreach (var (phone, serving) in phones)
        {
            await phone.SendAsync("""{"type":"ping","v":2}""");
            Assert.Equal("pong", FakePhone.TypeOf(await phone.NextAsync("pong")));
            Assert.False(serving.IsCompleted);
        }
    }

    [Fact]
    public async Task AFifthPhone_IsRefusedWithTooManyControllers_AtItsHello_WhenFourControl()
    {
        for (int i = 1; i <= 4; i++)
        {
            var (phone, _) = await _harness.HelloAsync(deviceId: $"phone-{i}");
            await phone.NextAsync("state");
        }

        var (fifth, serving) = await _harness.HelloAsync(deviceId: "phone-5");

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"too_many_controllers","message":"Too many devices are controlling this PC. Disconnect one and try again."}}""",
            await fifth.ReadAsync());
        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"too_many_controllers"}}""", await fifth.ReadAsync());
        await serving.WaitAsync(HangGuard, Token);
        Assert.Equal(4, Receiver.Phones.Count);
    }

    [Fact]
    public async Task ANewHelloOfAPhoneAlreadyConnected_TakesItsOwnPlace_NotAnothers()
    {
        var (older, olderServing) = await _harness.HelloAsync(deviceId: "phone-1");
        await older.NextAsync("state");
        for (int i = 2; i <= 4; i++)
        {
            var (phone, _) = await _harness.HelloAsync(deviceId: $"phone-{i}");
            await phone.NextAsync("state");
        }

        var (newer, _) = await _harness.HelloAsync(deviceId: "phone-1");

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"replaced"}}""", await older.NextAsync("close"));
        await olderServing.WaitAsync(HangGuard, Token);
        Assert.Equal("state", FakePhone.TypeOf(await newer.NextAsync("state")));
        Assert.Equal(4, Receiver.Phones.Count);
    }

    [Fact]
    public async Task AFifthPhone_TakesThePlaceOfAPassiveOne()
    {
        var (passive, passiveServing) = await _harness.HelloAsync(deviceId: "phone-1", passive: true);
        for (int i = 2; i <= 4; i++)
        {
            var (phone, _) = await _harness.HelloAsync(deviceId: $"phone-{i}");
            await phone.NextAsync("state");
        }

        var (fifth, _) = await _harness.HelloAsync(deviceId: "phone-5");

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"too_many_controllers"}}""", await passive.NextAsync("close"));
        await passiveServing.WaitAsync(HangGuard, Token);
        Assert.Equal("state", FakePhone.TypeOf(await fifth.NextAsync("state")));
    }

    [Fact]
    public async Task AControl_ReachesThePlayerOnTheUiThread_AndEveryPhoneGetsTheNewStatus()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        player.Answer = _ =>
        {
            player.State = player.State with { IsPlaying = false };
            return RemoteCommandResult.Done;
        };
        await _harness.AttachAsync(player);
        var (first, _) = await _harness.HelloAsync(deviceId: "phone-1");
        var (second, _) = await _harness.HelloAsync(deviceId: "phone-2", deviceName: "Elena's tablet");
        await first.NextAsync("state");
        await second.NextAsync("state");

        await first.SendAsync("""{"type":"control","control":{"name":"pause"},"v":2}""");

        Assert.Contains("\"isPlaying\":false", await first.NextAsync("state"), StringComparison.Ordinal);
        Assert.Contains("\"isPlaying\":false", await second.NextAsync("state"), StringComparison.Ordinal);
        Assert.Equal(SiloCastControls.Pause, Assert.Single(player.Applied).Name);
        Assert.Equal(0, player.OffThread);
    }

    [Fact]
    public async Task WithNoPlayerOpen_AControlGetsPlayerNotReady_AndStopIsSimplyDone()
    {
        var (phone, _) = await _harness.HelloAsync();
        await phone.NextAsync("state");

        await phone.SendAsync("""{"type":"control","control":{"name":"seek","seconds":120},"v":2}""");
        Assert.Equal("""{"type":"error","v":2,"error":{"code":"player_not_ready","message":"The PC player is not ready yet."}}""",
            await phone.ReadAsync());
        await phone.SendAsync("""{"type":"control","control":{"name":"stop"},"v":2}""");
        await phone.SendAsync("""{"type":"ping","v":2}""");

        Assert.Equal("pong", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
    }

    [Theory]
    [InlineData(RemoteCommandResult.Ignored, null)]
    [InlineData(RemoteCommandResult.NotReady, """{"type":"error","v":2,"error":{"code":"player_not_ready","message":"The PC player is not ready yet."}}""")]
    [InlineData(RemoteCommandResult.Failed, """{"type":"error","v":2,"error":{"code":"command_failed","message":"There is no next episode."}}""")]
    public async Task WhatThePlayerAnswers_IsWhatThePhoneHears(RemoteCommandResult answer, string? expected)
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher) { Answer = _ => answer };
        await _harness.AttachAsync(player);
        var (phone, _) = await _harness.HelloAsync();
        await phone.NextAsync("state");

        await phone.SendAsync("""{"type":"control","control":{"name":"play_next"},"v":2}""");
        await phone.SendAsync("""{"type":"ping","v":2}""");

        string first = await phone.ReadAsync() ?? "";
        Assert.Equal(expected ?? """{"type":"pong","v":2}""", first);
    }

    [Fact]
    public async Task AnUnknownControlName_AndAnUnknownType_AreSkipped()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        await _harness.AttachAsync(player);
        var (phone, _) = await _harness.HelloAsync();
        await phone.NextAsync("state");

        await phone.SendAsync("""{"type":"control","control":{"name":"set_hdr_enabled","enabled":true},"v":2}""");
        await phone.SendAsync("""{"type":"surprise","surprise":{},"v":2}""");
        await phone.SendAsync("""{"type":"ping","v":2}""");

        Assert.Equal("pong", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        Assert.Empty(player.Applied);
    }

    [Fact]
    public async Task AKnownTypeWithAMalformedPayload_EndsThatConnectionOnly()
    {
        var (broken, brokenServing) = await _harness.HelloAsync(deviceId: "phone-1");
        var (other, _) = await _harness.HelloAsync(deviceId: "phone-2");
        await broken.NextAsync("state");

        await broken.SendAsync("""{"type":"control","control":{"seconds":3},"v":2}""");

        Assert.Equal("""{"type":"close","v":2}""", await broken.NextAsync("close"));
        await brokenServing.WaitAsync(HangGuard, Token);
        await other.SendAsync("""{"type":"ping","v":2}""");
        Assert.Equal("pong", FakePhone.TypeOf(await other.NextAsync("pong")));
    }

    [Fact]
    public async Task TheStatus_GoesOutEveryHalfSecond_WhileATitleIsOpen_AndOnceWhenIdle()
    {
        var (phone, _) = await _harness.HelloAsync();
        Assert.Contains("\"title\":\"Ready\"", await phone.NextAsync("state"), StringComparison.Ordinal);

        // Idle: nothing more, however long.
        Time.Advance(TimeSpan.FromMilliseconds(2900));
        await _harness.SettleUiAsync();
        await AssertNothingWaitingAsync(phone);

        var player = new FakeRemotePlayer(_harness.Dispatcher);
        await _harness.AttachAsync(player);
        Assert.Contains("Example Movie", await phone.ReadAsync(), StringComparison.Ordinal);
        for (int tick = 0; tick < 3; tick++)
        {
            player.State = player.State with { CurrentTime = 121 + tick };
            Time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Contains($"\"currentTime\":{121 + tick}", await phone.NextAsync("state"), StringComparison.Ordinal);
        }

        await _harness.DetachAsync(player);
        Assert.Contains("\"title\":\"Ready\"", await phone.NextAsync("state"), StringComparison.Ordinal);
        Time.Advance(TimeSpan.FromMilliseconds(1000));
        await _harness.SettleUiAsync();
        await AssertNothingWaitingAsync(phone);
    }

    [Fact]
    public async Task AChangeInThePlayer_IsSentAtOnce()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        await _harness.AttachAsync(player);
        var (phone, _) = await _harness.HelloAsync();
        await phone.NextAsync("state");

        await _harness.Dispatcher.InvokeAsync(() =>
        {
            player.State = player.State with { IsMuted = true };
            player.RaiseStateChanged();
            return true;
        });

        Assert.Contains("\"isMuted\":true", await phone.NextAsync("state"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThanTheBudget_GetsRateLimited_AtMostOnceASecond_AndThePlayerIsNotAsked()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        await _harness.AttachAsync(player);
        var (phone, _) = await _harness.HelloAsync();
        await phone.NextAsync("state");

        for (int i = 0; i < 45; i++)
        {
            await phone.SendAsync("""{"type":"control","control":{"name":"seek","seconds":10},"v":2}""");
        }
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");

        // The hello took one of the forty.
        Assert.Equal(39, player.Applied.Count);
        Assert.Single(phone.Received, message => message.Contains("rate_limited", StringComparison.Ordinal));
        Assert.Contains("""{"type":"error","v":2,"error":{"code":"rate_limited","message":"Too many commands. Slow down and try again."}}""",
            phone.Received);

        Time.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 22; i++)
        {
            await phone.SendAsync("""{"type":"control","control":{"name":"seek","seconds":10},"v":2}""");
        }
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");

        Assert.Equal(59, player.Applied.Count);
        Assert.Equal(2, phone.Received.Count(message => message.Contains("rate_limited", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EveryMessageButTheHeartbeat_CountsAgainstThePhonesBudget()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        await _harness.AttachAsync(player);
        var (phone, _) = await _harness.HelloAsync();
        await phone.NextAsync("state");

        // The hello took one of the forty; offers, presence and launches take theirs too.
        for (int i = 0; i < 37; i++)
        {
            await phone.SendAsync($$"""{"type":"handoff_offer","handoffOffer":{"requestId":"request-{{i}}","serverId":"{{RemoteHarness.ServerId}}","serverURL":"https://silo.example","profileId":"7"},"v":2}""");
        }
        await phone.SendAsync("""{"type":"presence","presence":{"passive":false},"v":2}""");
        await phone.SendAsync($$$"""{"type":"launch","launch":{"serverId":"{{{RemoteHarness.ServerId}}}","playback":{"contentId":"movie-1","startFromBeginning":true}},"v":2}""");
        // Over the budget: an offer and a command go unanswered but for one rate limit.
        await phone.SendAsync($$"""{"type":"handoff_offer","handoffOffer":{"requestId":"request-over","serverId":"{{RemoteHarness.ServerId}}","serverURL":"https://silo.example","profileId":"7"},"v":2}""");
        await phone.SendAsync("""{"type":"control","control":{"name":"pause"},"v":2}""");
        // The heartbeat is never budgeted.
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");

        var received = phone.Received;
        Assert.Equal(37, received.Count(message => FakePhone.TypeOf(message) == "handoff_cancel"));
        Assert.Single(received, message => message.Contains("handoff_required", StringComparison.Ordinal));
        Assert.Single(received, message => message.Contains("rate_limited", StringComparison.Ordinal));
        Assert.DoesNotContain(received, message => message.Contains("request-over", StringComparison.Ordinal));
        Assert.Empty(player.Applied);
    }

    [Fact]
    public async Task RepliesToAPhoneOnAnotherServer_CountAgainstItsBudget()
    {
        var (phone, _) = await _harness.HelloAsync(SiloCastServerId.From(new Uri("https://other.example")));

        for (int i = 0; i < 45; i++)
        {
            await phone.SendAsync("""{"type":"control","control":{"name":"pause"},"v":2}""");
        }
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");

        var received = phone.Received;
        Assert.Equal(39, received.Count(message => message.Contains("\"code\":\"unauthorized\"", StringComparison.Ordinal)));
        Assert.Single(received, message => message.Contains("rate_limited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APresenceThatChangesNothing_ChangesNoPhones()
    {
        int changes = 0;
        Receiver.PhonesChanged += (_, _) => changes++;
        var (phone, _) = await _harness.HelloAsync();
        await phone.NextAsync("state");
        await _harness.SettleUiAsync();
        int afterHello = changes;

        for (int i = 0; i < 5; i++)
        {
            await phone.SendAsync("""{"type":"presence","presence":{"passive":false},"v":2}""");
        }
        await phone.SendAsync("""{"type":"presence","presence":{"passive":true},"v":2}""");
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");
        await _harness.SettleUiAsync();

        Assert.Equal(afterHello + 1, changes);
        Assert.False(Assert.Single(Receiver.Phones).IsActive);
    }

    [Fact]
    public async Task AnOffer_IsRefusedForNow()
    {
        var (phone, _) = await _harness.HelloAsync();

        await phone.SendAsync($$"""{"type":"handoff_offer","handoffOffer":{"requestId":"request-1","serverId":"{{RemoteHarness.ServerId}}","serverURL":"https://silo.example","profileId":"7","profileName":"Elena","replaceConfirmed":true},"v":2}""");

        string cancel = await phone.NextAsync("handoff_cancel");
        Assert.Contains("\"requestId\":\"request-1\"", cancel, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"handoff_failed\"", cancel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOfferFromAnotherServer_NamesTheServerThisPcPlaysFrom()
    {
        string otherServer = SiloCastServerId.From(new Uri("https://other.example"));
        var (phone, _) = await _harness.HelloAsync(otherServer);

        await phone.SendAsync($$"""{"type":"handoff_offer","handoffOffer":{"requestId":"request-1","serverId":"{{otherServer}}","serverURL":"https://other.example","profileId":"7"},"v":2}""");

        Assert.Equal("""{"type":"handoff_cancel","v":2,"handoffCancel":{"requestId":"request-1","reason":"handoff_failed","message":"This PC plays from Example Server."}}""",
            await phone.NextAsync("handoff_cancel"));
    }

    [Fact]
    public async Task AnOfferBeforeTheHello_IsRefusedAsAnOldPhones()
    {
        var (phone, _, _) = await _harness.ServeAsync();

        await phone.SendAsync($$"""{"type":"handoff_offer","handoffOffer":{"requestId":"request-1","serverId":"{{RemoteHarness.ServerId}}","serverURL":"https://silo.example","profileId":"7"},"v":2}""");

        Assert.Equal("""{"type":"handoff_cancel","v":2,"handoffCancel":{"requestId":"request-1","reason":"version_unsupported","message":"Update Silo on both devices to continue."}}""",
            await phone.NextAsync("handoff_cancel"));
    }

    [Fact]
    public async Task ALaunch_IsRefusedWithHandoffRequired()
    {
        var (phone, _) = await _harness.HelloAsync();

        await phone.SendAsync($$$"""{"type":"launch","launch":{"serverId":"{{{RemoteHarness.ServerId}}}","playback":{"contentId":"movie-1","startFromBeginning":false,"resumePosition":4747.284}},"v":2}""");

        Assert.Equal("""{"type":"error","v":2,"error":{"code":"handoff_required","message":"Prepare the phone profile before playing."}}""",
            await phone.NextAsync("error"));
    }

    [Fact]
    public async Task APassivePhone_BecomesActiveWithPresence()
    {
        var connected = new List<ConnectedPhone>();
        Receiver.PhoneConnected += (_, phone) => connected.Add(phone);
        var (phone, _) = await _harness.HelloAsync(passive: true);
        await phone.NextAsync("state");
        await _harness.SettleUiAsync();
        Assert.Empty(connected);
        Assert.False(Assert.Single(Receiver.Phones).IsActive);

        await phone.SendAsync("""{"type":"presence","presence":{"passive":false},"v":2}""");
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");
        await _harness.SettleUiAsync();

        Assert.Equal(new ConnectedPhone(1, "Elena's phone", IsActive: true), Assert.Single(connected));
        Assert.True(Assert.Single(Receiver.Phones).IsActive);
    }

    [Fact]
    public async Task AnActivePhonesHello_IsAnnouncedOnce_AndEveryChangeToThePhones()
    {
        var connected = new List<ConnectedPhone>();
        int changes = 0;
        Receiver.PhoneConnected += (_, phone) => connected.Add(phone);
        Receiver.PhonesChanged += (_, _) => changes++;

        var (phone, serving) = await _harness.HelloAsync();
        await phone.NextAsync("state");
        await _harness.SettleUiAsync();
        Assert.Single(connected);
        Assert.True(changes >= 1);
        int before = changes;

        await phone.SendAsync("""{"type":"close","v":2}""");
        await serving.WaitAsync(HangGuard, Token);
        await _harness.SettleUiAsync();

        Assert.Empty(Receiver.Phones);
        Assert.True(changes > before);
        Assert.Single(connected);
    }

    [Fact]
    public async Task APhonesName_IsCleanedBeforeItIsShown()
    {
        string name = "Elena‮'s\u0007 phone​" + new string('x', 100);

        var (phone, _) = await _harness.HelloAsync(deviceName: name);
        await phone.NextAsync("state");

        string shown = Assert.Single(Receiver.Phones).Name;
        Assert.StartsWith("Elena's phone", shown, StringComparison.Ordinal);
        Assert.DoesNotContain('‮', shown);
        Assert.DoesNotContain('\u0007', shown);
        Assert.DoesNotContain('​', shown);
        Assert.Equal(64 + 1, shown.Length);
        Assert.EndsWith("…", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectAsync_ClosesThatPhoneWithTvDisconnected()
    {
        var (first, firstServing) = await _harness.HelloAsync(deviceId: "phone-1");
        var (second, _) = await _harness.HelloAsync(deviceId: "phone-2");
        await first.NextAsync("state");

        await Receiver.DisconnectAsync(1);

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await first.NextAsync("close"));
        await firstServing.WaitAsync(HangGuard, Token);
        Assert.Equal(2, Assert.Single(Receiver.Phones).Number);
        await second.SendAsync("""{"type":"ping","v":2}""");
        await second.NextAsync("pong");
    }

    [Fact]
    public async Task CloseAllAsync_TellsEveryPhone()
    {
        var (first, firstServing) = await _harness.HelloAsync(deviceId: "phone-1");
        var (second, secondServing) = await _harness.HelloAsync(deviceId: "phone-2");
        await first.NextAsync("state");
        await second.NextAsync("state");

        await Receiver.CloseAllAsync(SiloCastCloseReasons.TvDisconnected);

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await first.NextAsync("close"));
        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await second.NextAsync("close"));
        await Task.WhenAll(firstServing, secondServing).WaitAsync(HangGuard, Token);
        Assert.Empty(Receiver.Phones);
    }

    [Fact]
    public async Task TheSessionTheRealPhoneRan_RunsAgain()
    {
        var player = new FakeRemotePlayer(_harness.Dispatcher);
        player.Answer = command =>
        {
            if (command is { Name: SiloCastControls.Seek, Seconds: { } seconds })
            {
                player.State = player.State with { CurrentTime = seconds };
            }
            return RemoteCommandResult.Done;
        };
        await _harness.AttachAsync(player);

        // The hello the real phone sent, with sample values: payload first, close_reason, resume_token, replace_confirmation.
        var (phone, serving) = await _harness.HelloAsync(serverIdentity: RemoteHarness.ServerIdentity);
        Assert.Contains("Example Movie", await phone.NextAsync("state"), StringComparison.Ordinal);
        for (int beat = 0; beat < 3; beat++)
        {
            Time.Advance(TimeSpan.FromSeconds(3));
            await phone.NextAsync("ping");
        }
        await phone.SendAsync("""{"type":"control","control":{"name":"seek","seconds":1798.292724609375},"v":2}""");
        // Statuses of the ticks before it may still wait; the one after the seek carries the new position.
        while (!(await phone.NextAsync("state")).Contains("\"currentTime\":1798.292724609375", StringComparison.Ordinal))
        {
        }
        await phone.SendAsync("""{"type":"close","v":2}""");
        await serving.WaitAsync(HangGuard, Token);

        // Two seconds later, a new handshake under the same key.
        Time.Advance(TimeSpan.FromSeconds(2));
        var (again, _) = await _harness.HelloAsync(serverIdentity: RemoteHarness.ServerIdentity);
        Assert.Contains("Example Movie", await again.NextAsync("state"), StringComparison.Ordinal);
        Assert.Equal(0, phone.OpenFailures + again.OpenFailures);
        Assert.Equal(SiloCastControls.Seek, Assert.Single(player.Applied).Name);
    }

    // Nothing waits for the phone: the answer to its ping is the next thing it reads.
    private static async Task AssertNothingWaitingAsync(FakePhone phone)
    {
        await phone.SendAsync("""{"type":"ping","v":2}""");
        Assert.Equal("""{"type":"pong","v":2}""", await phone.ReadAsync());
    }
}
