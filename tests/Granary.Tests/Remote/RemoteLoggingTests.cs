using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Granary.App.Services;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Remote;
using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// What the phone remote writes to the log (Review Focus 4), every category at every level, over three runs on fakes:
/// everything it does with phones (pairing, a wrong key, the hello, the status, commands, an offer, a launch, a reset, a
/// change of server and the stop); everything that can fail, with exception messages that hold the key, the link, the
/// server's address and a title; and every limit and every way a connection or the receiver ends. The App's accept loop
/// runs with such messages too. No line may hold anything of the never-list (design 6.3), every connection's end is
/// logged by its number and a reason of the fixed list, and every reason of that list is met.
/// </summary>
public sealed partial class RemoteLoggingTests : IAsyncDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static readonly IPAddress OnePeer = IPAddress.Parse("192.168.1.30");

    private readonly List<RemoteHarness> _harnesses = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var harness in _harnesses)
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task NothingSecretOrPersonal_ReachesTheLog()
    {
        var run = await EverythingAsync();

        AssertNothingSecret(run);
        string log = run.All;
        // An offer made ready, a launch, and a refused offer and launch were met, so the check above covers them.
        Assert.Contains("an offer to start a title is ready", log, StringComparison.Ordinal);
        Assert.Contains("a launch opens the player", log, StringComparison.Ordinal);
        Assert.Contains("an offer to start a title was refused (another profile)", log, StringComparison.Ordinal);
        Assert.Contains("a launch was refused (no ready offer)", log, StringComparison.Ordinal);
        Assert.Contains("a launch was refused (the PC is locked)", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingSecretOrPersonal_ReachesTheLog_WhenThingsFail()
    {
        var run = await FailuresAsync();

        AssertNothingSecret(run);
        string log = run.All;
        // Each failure was met, so the check above is not empty.
        Assert.Contains("cannot read its pairing key (IOException", log, StringComparison.Ordinal);
        Assert.Contains("could not be reset (IOException", log, StringComparison.Ordinal);
        Assert.Contains("A listener of the pairing code's reset failed (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("the player failed a command (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("a message failed (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("advertisement failed (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("the status could not be made (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("a message too large for a frame was not sent", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingSecretOrPersonal_ReachesTheLog_AtEveryLimit()
    {
        AssertNothingSecret(await LimitsAsync());
    }

    [Fact]
    public async Task NothingSecretOrPersonal_ReachesTheLog_FromTheAppsListener()
    {
        var logs = new LogSink();
        var accepts = Channel.CreateUnbounded<object>();
        var served = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string secret = Secret(RemoteHarness.Key);
        int handed = 0;
        var loop = new RemoteAcceptLoop(async token =>
        {
            object next = await accepts.Reader.ReadAsync(token);
            return next is Exception failure ? throw failure : (IRemoteConnection)next;
        }, connection =>
        {
            if (Interlocked.Increment(ref handed) == 1)
            {
                throw new InvalidOperationException(secret);
            }
            served.TrySetResult();
            return Task.FromException(new InvalidOperationException(secret));
        }, () => throw new InvalidOperationException(secret), new ManualTimeProvider(), logs.For<RemoteAcceptLoop>());
        using var first = new DuplexPipe();
        using var second = new DuplexPipe();
        loop.Start();

        accepts.Writer.TryWrite(new IOException(secret));
        accepts.Writer.TryWrite(first.Connection(OnePeer));
        accepts.Writer.TryWrite(second.Connection(OnePeer));
        await served.Task.WaitAsync(HangGuard, Token);
        accepts.Writer.TryWrite(new InvalidOperationException(secret));
        await loop.Running.WaitAsync(HangGuard, Token);

        string log = logs.All;
        Assert.Contains("could not be taken over (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("A phone's connection failed (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("stopped taking connections (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("could not be told its listener failed (InvalidOperationException", log, StringComparison.Ordinal);
        Assert.All(SecretParts(RemoteHarness.Key).Concat(["192.168.1.30", "192.168.1.20", "52817"]),
            value => Assert.DoesNotContain(value, log, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("{", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryConnectionsEnd_IsLoggedWithItsNumberAndAReason()
    {
        var everything = await EverythingAsync();
        var failures = await FailuresAsync();
        var limits = await LimitsAsync();

        foreach (var lines in everything.Logs.Concat(failures.Logs).Concat(limits.Logs))
        {
            AssertEveryEndLogged(lines);
        }
        // A connection that never opened ends logged all the same: the one with the wrong key.
        var ended = everything.Logs.Single().Select(line => Ended().Match(line)).Where(match => match.Success).ToList();
        var opened = everything.Logs.Single().Select(line => Opened().Match(line)).Where(match => match.Success)
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(ended, end => end.Groups[2].Value.Trim() == RemoteLogReasons.WrongKey && !opened.Contains(end.Groups[1].Value));
    }

    [Fact]
    public async Task EveryReasonOfTheList_IsLoggedBySomeScenario()
    {
        var runs = new[] { await EverythingAsync(), await FailuresAsync(), await LimitsAsync() };

        var logged = runs.SelectMany(run => run.Logs).SelectMany(lines => lines).Select(line => Reason().Match(line))
            .Where(match => match.Success).Select(match => match.Groups[1].Value.Trim()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(AllReasons().Order(StringComparer.Ordinal), logged.Order(StringComparer.Ordinal));
    }

    // The phone remote end to end.
    private async Task<Run> EverythingAsync()
    {
        var harness = Harness(lifetime: true);
        var receiver = harness.Receiver;
        string otherServerId = SiloCastServerId.From(new Uri("https://other.example"));
        var neverLogged = new List<string>
        {
            "silo.example", "other.example", RemoteHarness.ServerId, otherServerId, RemoteHarness.ServerIdentity, "deploy-2",
            "Example Server", "Elena", "phone-1", "phone-2", "movie-1", "movie-2", "Example Movie", "resume-token-1", "request-1",
            "request-2", "evil.example",
        };
        await harness.StartListeningAsync();
        var player = new FakeRemotePlayer(harness.Dispatcher)
        {
            Answer = command => command.Name == SiloCastControls.PlayNext ? RemoteCommandResult.Failed : RemoteCommandResult.Done,
        };
        await harness.AttachAsync(player);
        neverLogged.Add(receiver.PairingLink!);
        neverLogged.AddRange(KeyParts(RemoteHarness.KeyOf(harness.PairingStore.Current!)));

        // A phone with another key is refused at its first frame.
        var (stranger, _, strangerServing) = await harness.ConnectThroughListenerAsync(RemoteHarness.OtherKey, pair: false);
        Assert.False(await stranger.PairAsync());
        await strangerServing.WaitAsync(HangGuard, Token);
        neverLogged.AddRange(KeyParts(RemoteHarness.OtherKey));

        // The phone pairs, says hello, gets the status, controls the player, offers a title and launches one.
        var (phone, _, serving) = await harness.ConnectThroughListenerAsync();
        AddNonces(neverLogged, phone);
        await phone.NextAsync("hello");
        await phone.HelloAsync(RemoteHarness.ServerId, RemoteHarness.ServerIdentity);
        await phone.NextAsync("state");
        foreach (string control in new[]
        {
            """{"name":"seek","seconds":1798.292724609375}""", """{"name":"pause"}""", """{"name":"set_volume","volume":0.4}""",
            """{"name":"select_subtitle_track","trackId":2}""", """{"name":"play_next"}""", """{"name":"set_hdr_enabled","enabled":true}""",
        })
        {
            await phone.SendAsync($$"""{"type":"control","control":{{control}},"v":2}""");
        }
        await phone.SendAsync($$"""{"type":"handoff_offer","handoffOffer":{"requestId":"request-1","serverId":"{{RemoteHarness.ServerId}}","serverURL":"https://evil.example","profileId":"7","profileName":"Elena Moreau","resumeToken":"resume-token-1"},"v":2}""");
        string ready = await phone.NextAsync("handoff_ready");
        using (var answer = JsonDocument.Parse(ready))
        {
            neverLogged.Add(answer.RootElement.GetProperty("handoffReady").GetProperty("resumeToken").GetString()!);
        }
        await phone.SendAsync($$$"""{"type":"launch","launch":{"serverId":"{{{RemoteHarness.ServerId}}}","playback":{"contentId":"movie-1","startFromBeginning":false,"resumePosition":4747.284}},"v":2}""");
        // An offer for another profile is refused, and so is a launch while the PC is locked.
        await phone.SendAsync($$"""{"type":"handoff_offer","handoffOffer":{"requestId":"request-2","serverId":"{{RemoteHarness.ServerId}}","serverURL":"https://silo.example","profileId":"8","profileName":"Elena Moreau"},"v":2}""");
        await phone.NextAsync("handoff_cancel");
        harness.Lock.IsLocked = true;
        await phone.SendAsync($$$"""{"type":"launch","launch":{"serverId":"{{{RemoteHarness.ServerId}}}","playback":{"contentId":"movie-2","startFromBeginning":true}},"v":2}""");
        await phone.NextAsync("error");
        harness.Lock.IsLocked = false;
        await phone.SendAsync("""{"type":"ping","v":2}""");
        await phone.NextAsync("pong");
        Assert.Single(harness.Navigation.Navigations);

        // Reset Pairing Code lets the phone go; a phone on the new code says hello again.
        Assert.Equal(PairingResetOutcome.Reset, await receiver.ResetPairingAsync());
        await serving.WaitAsync(HangGuard, Token);
        neverLogged.AddRange(KeyParts(RemoteHarness.KeyOf(harness.PairingStore.Current!)));
        neverLogged.Add(receiver.PairingLink!);
        var (again, _, againServing) = await harness.ConnectThroughListenerAsync();
        AddNonces(neverLogged, again);
        await again.NextAsync("hello");
        await again.HelloAsync(RemoteHarness.ServerId, RemoteHarness.ServerIdentity, deviceId: "phone-2");
        await again.NextAsync("state");
        // Its readiness went with the old connection: a launch without an offer is refused.
        await again.SendAsync($$$"""{"type":"launch","launch":{"serverId":"{{{RemoteHarness.ServerId}}}","playback":{"contentId":"movie-2","startFromBeginning":true}},"v":2}""");
        Assert.Contains("handoff_required", await again.NextAsync("error"), StringComparison.Ordinal);

        // Another server: the receiver stops and starts for it; then Granary closes.
        harness.SignIn(RemoteHarness.Server("https://other.example", "entry-2", "deploy-2"));
        await SettleAsync(harness);
        await againServing.WaitAsync(HangGuard, Token);
        neverLogged.Add(receiver.PairingLink!);
        await harness.DetachAsync(player);
        await StopAsync(harness);
        return new Run([[.. harness.Logs.Lines]], neverLogged);
    }

    // Everything that can fail, each failure's message holding what the log must never hold.
    private async Task<Run> FailuresAsync()
    {
        var harness = Harness(lifetime: true);
        var receiver = harness.Receiver;
        string secret = Secret(RemoteHarness.Key);
        var neverLogged = new List<string>(SecretParts(RemoteHarness.Key)) { "Example Server 2" };

        // The key from before cannot be read for now: nothing starts.
        harness.Secrets.Values[RemotePairing.SecretKey] = Convert.ToBase64String(RemoteHarness.Key);
        harness.Secrets.FailReads = new IOException(secret);
        await harness.StartListeningAsync();
        Assert.Equal(RemoteReceiverState.Failed, receiver.Status.State);
        harness.Secrets.FailReads = null;
        harness.Addresses.Set(RemoteHarness.Lan);
        await SettleAsync(harness);
        Assert.Equal(RemoteReceiverState.Listening, receiver.Status.State);
        neverLogged.Add(receiver.PairingLink!);

        // A reset that cannot be saved, then one whose news a listener fails to take.
        harness.Secrets.FailWrites = new IOException(secret);
        Assert.Equal(PairingResetOutcome.Failed, await receiver.ResetPairingAsync());
        harness.Secrets.FailWrites = null;
        harness.PairingStore.KeyChanged += (_, _) => throw new InvalidOperationException(secret);
        Assert.Equal(PairingResetOutcome.Reset, await receiver.ResetPairingAsync());
        await SettleAsync(harness);
        neverLogged.AddRange(KeyParts(RemoteHarness.KeyOf(harness.PairingStore.Current!)));
        neverLogged.Add(receiver.PairingLink!);

        // A player that fails a command, then one whose status cannot be read after a command.
        var player = new TroubledPlayer(secret);
        await harness.AttachAsync(player);
        var (phone, _, serving) = await harness.ConnectThroughListenerAsync();
        AddNonces(neverLogged, phone);
        await phone.NextAsync("hello");
        await phone.HelloAsync(RemoteHarness.ServerId, RemoteHarness.ServerIdentity);
        await phone.NextAsync("state");
        player.ApplyThrows = true;
        await phone.SendAsync("""{"type":"control","control":{"name":"pause"},"v":2}""");
        Assert.Contains("command_failed", await phone.NextAsync("error"), StringComparison.Ordinal);
        player.ApplyThrows = false;
        player.StateThrows = true;
        await phone.SendAsync("""{"type":"control","control":{"name":"pause"},"v":2}""");
        await serving.WaitAsync(HangGuard, Token);
        player.StateThrows = false;

        // Windows' advertisement fails while the receiver runs (a new server name), and again at the next start.
        var (watcher, _, watching) = await harness.ConnectThroughListenerAsync();
        await watcher.NextAsync("hello");
        await watcher.HelloAsync(RemoteHarness.ServerId, RemoteHarness.ServerIdentity, deviceId: "phone-2");
        await watcher.NextAsync("state");
        harness.Advertiser.Throws = new InvalidOperationException(secret);
        harness.SignIn(new ServerEntry("entry-1", new Uri("https://silo.example"), "Example Server 2", RemoteHarness.ServerIdentity,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "7"));
        await SettleAsync(harness);
        await watching.WaitAsync(HangGuard, Token);
        harness.Addresses.Set(RemoteHarness.Lan);
        await SettleAsync(harness);
        Assert.Equal(RemoteReceiverState.Failed, receiver.Status.State);
        await harness.DetachAsync(player);
        await StopAsync(harness);

        // A connection whose status cannot be made, and a message too large for a frame.
        var controllers = Harness();
        var (controller, controlled, _, running) = await controllers.OpenAsync();
        controller.OfferState(() => throw new InvalidOperationException(secret));
        await controlled.SendAsync("""{"type":"ping","v":2}""");
        await controlled.NextAsync("pong");
        await controller.SendAsync(System.Text.Encoding.UTF8.GetBytes(secret + new string('x', SiloCastFrame.MaxFrameBytes)));
        await controller.CloseAsync(null, RemoteLogReasons.Stopped);
        await running.WaitAsync(HangGuard, Token);
        return new Run([[.. harness.Logs.Lines], [.. controllers.Logs.Lines]], neverLogged);
    }

    // Every limit, and every way a connection or the receiver ends.
    private async Task<Run> LimitsAsync()
    {
        var neverLogged = new List<string>
        {
            "silo.example", RemoteHarness.ServerId, "Elena", "phone-", "192.168.1.", "8.8.8.8", "Example Movie", "movie-1",
        };
        neverLogged.AddRange(KeyParts(RemoteHarness.Key));
        var logs = new List<IReadOnlyList<string>>
        {
            await RefusalsAsync(),
            await PhonesAsync(),
            await DeadlinesAsync(),
            await SlowReaderAsync(),
            await LifetimeEndsAsync(),
        };
        return new Run(logs, neverLogged);
    }

    // Connections refused or let go before a phone said who it is, and phones that break the rules.
    private async Task<IReadOnlyList<string>> RefusalsAsync()
    {
        var harness = Harness();
        var serving = new List<Task>();

        // From outside the local network; then 21 in a minute from one address, each beyond two waiting closing the oldest.
        serving.Add((await harness.ServeAsync(pair: false, peer: IPAddress.Parse("8.8.8.8"))).Serving);
        for (int i = 0; i <= RemoteReceiverPolicy.MaxNewConnectionsPerMinute; i++)
        {
            serving.Add((await harness.ServeAsync(pair: false, peer: OnePeer)).Serving);
        }

        // A plain hello first, and a first frame over its limit.
        var (plain, _, plainServing) = await harness.ServeAsync(pair: false);
        await plain.SendPlainAsync("""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":[2]},"v":2}""");
        var (large, _, largeServing) = await harness.ServeAsync(pair: false);
        await large.SendLengthAsync(SiloCastFrame.MaxFirstFrameBytes + 1);
        await Task.WhenAll(plainServing, largeServing).WaitAsync(HangGuard, Token);

        // Keyed, then: a frame over its limit, a flood, a frame played back, and a hello that is not a phone's.
        var (huge, _, hugeServing) = await harness.ServeAsync();
        await huge.SendLengthAsync(SiloCastFrame.MaxFrameBytes + 1);
        var (flood, _, floodServing) = await harness.ServeAsync();
        await FloodAsync(flood);
        var (replay, _, replayServing) = await harness.ServeAsync();
        await replay.SendAsync("""{"type":"ping","v":2}""");
        await replay.ReplaySealedFrameAsync();
        var (tv, _, tvServing) = await harness.ServeAsync();
        await tv.NextAsync("hello");
        await tv.SendAsync($$"""{"type":"hello","hello":{"role":"tv","deviceName":"Elena's TV","deviceId":"tv-1","supportedVersions":[2],"serverId":"{{RemoteHarness.ServerId}}"},"v":2}""");
        await Task.WhenAll(hugeServing, floodServing, replayServing, tvServing).WaitAsync(HangGuard, Token);

        // Phones that said hello: a malformed command, the phone's close, the phone gone, a newer connection of the same.
        var (malformed, malformedServing) = await harness.HelloAsync(deviceId: "phone-1");
        await malformed.SendAsync("""{"type":"control","control":{"seconds":3},"v":2}""");
        var (closing, closingServing) = await harness.HelloAsync(deviceId: "phone-2");
        await closing.SendAsync("""{"type":"close","v":2}""");
        var (leaving, leavingServing) = await harness.HelloAsync(deviceId: "phone-3");
        await leaving.NextAsync("state");
        await leaving.DisposeAsync();
        var (older, olderServing) = await harness.HelloAsync(deviceId: "phone-4");
        await older.NextAsync("state");
        await harness.HelloAsync(deviceId: "phone-4");
        await Task.WhenAll(malformedServing, closingServing, leavingServing, olderServing).WaitAsync(HangGuard, Token);

        await harness.Receiver.CloseAllAsync(SiloCastCloseReasons.TvDisconnected);
        await Task.WhenAll(serving).WaitAsync(HangGuard, Token);
        return [.. harness.Logs.Lines];
    }

    // The four places among the phones: a fifth takes a passive one's, a sixth is refused, and the PC lets one go.
    private async Task<IReadOnlyList<string>> PhonesAsync()
    {
        var harness = Harness();
        var (passive, passiveServing) = await harness.HelloAsync(deviceId: "phone-1", passive: true);
        for (int i = 2; i <= 4; i++)
        {
            var (phone, _) = await harness.HelloAsync(deviceId: $"phone-{i}");
            await phone.NextAsync("state");
        }
        var (fifth, _) = await harness.HelloAsync(deviceId: "phone-5");
        await fifth.NextAsync("state");
        await passiveServing.WaitAsync(HangGuard, Token);
        var (sixth, sixthServing) = await harness.HelloAsync(deviceId: "phone-6");
        await sixthServing.WaitAsync(HangGuard, Token);
        Assert.Contains("too_many_controllers", await sixth.NextAsync("error"), StringComparison.Ordinal);

        await harness.Receiver.DisconnectAsync(harness.Receiver.Phones[0].Number);
        await harness.Receiver.CloseAllAsync(SiloCastCloseReasons.TvDisconnected);
        await harness.SettleUiAsync();
        return [.. harness.Logs.Lines];
    }

    // No pairing hello and no hello within 5 seconds; no answer to pings.
    private async Task<IReadOnlyList<string>> DeadlinesAsync()
    {
        var harness = Harness();
        var (_, _, silentServing) = await harness.ServeAsync(pair: false);
        var (_, _, keyedServing) = await harness.ServeAsync();
        var (deaf, deafServing) = await harness.HelloAsync();
        await deaf.NextAsync("state");
        deaf.AnswersPings = false;

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await Task.WhenAll(silentServing, keyedServing).WaitAsync(HangGuard, Token);
        for (int beat = 0; beat < 3; beat++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(3));
        }
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await deafServing.WaitAsync(HangGuard, Token);
        return [.. harness.Logs.Lines];
    }

    // A phone that reads nothing for 10 seconds.
    private async Task<IReadOnlyList<string>> SlowReaderAsync()
    {
        var harness = Harness();
        var (controller, _, pipe, running) = await harness.OpenAsync(pauseAt: 1024);
        controller.SaidHello = true;
        pipe.PhoneStopsReading();
        var sending = controller.SendAsync(SiloCastMessages.Error("large", new string('x', 8 * 1024))).AsTask();

        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await running.WaitAsync(HangGuard, Token);
        await sending.WaitAsync(HangGuard, Token);
        return [.. harness.Logs.Lines];
    }

    // The receiver stops: switched off, signed out, off the network, its listener failed, no port at a new address; each
    // with a phone on it.
    private async Task<IReadOnlyList<string>> LifetimeEndsAsync()
    {
        var harness = Harness(lifetime: true);
        await harness.StartListeningAsync();

        await WithAPhoneAsync(harness, () => harness.Preferences.SetPhoneRemoteAsync(false));
        await harness.Preferences.SetPhoneRemoteAsync(true);
        await SettleAsync(harness);

        await WithAPhoneAsync(harness, () =>
        {
            harness.Session.ProfileId = null;
            harness.Session.State = SessionState.ProfileRequired;
            harness.Session.RaiseChanged();
            return Task.CompletedTask;
        });
        harness.SignIn();
        await SettleAsync(harness);

        await WithAPhoneAsync(harness, () =>
        {
            harness.Addresses.Set(null);
            return Task.CompletedTask;
        });
        harness.Addresses.Set(RemoteHarness.Lan);
        await SettleAsync(harness);

        await WithAPhoneAsync(harness, () =>
        {
            harness.Listener.Fail();
            return Task.CompletedTask;
        });
        Assert.Equal(RemoteReceiverState.Failed, harness.Receiver.Status.State);
        harness.Addresses.Set(RemoteHarness.Lan);
        await SettleAsync(harness);

        await WithAPhoneAsync(harness, () =>
        {
            harness.Listener.NoPortFree = true;
            harness.Addresses.Set(new LanAddress(IPAddress.Parse("192.168.1.21"), 7));
            return Task.CompletedTask;
        });
        Assert.Equal(RemoteReceiverState.NoPort, harness.Receiver.Status.State);
        await StopAsync(harness);
        return [.. harness.Logs.Lines];
    }

    // A phone connected and said hello; then the change, and the phone let go.
    private static async Task WithAPhoneAsync(RemoteHarness harness, Func<Task> change)
    {
        var (phone, _, serving) = await harness.ConnectThroughListenerAsync();
        await phone.NextAsync("hello");
        await phone.HelloAsync(RemoteHarness.ServerId, RemoteHarness.ServerIdentity);
        await phone.NextAsync("state");
        await change();
        await SettleAsync(harness);
        await serving.WaitAsync(HangGuard, Token);
    }

    // More frames than the frame budget lets through; the receiver may close before the last.
    private static async Task FloodAsync(FakePhone phone)
    {
        for (int i = 0; i <= FrameBudget.FrameBurst; i++)
        {
            try
            {
                await phone.SendAsync("""{"type":"surprise","v":2}""");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private RemoteHarness Harness(bool lifetime = false)
    {
        var harness = new RemoteHarness(lifetime);
        _harnesses.Add(harness);
        return harness;
    }

    // What the UI thread was given has run, the receiver has looked at it, and what it raised has run too.
    private static async Task SettleAsync(RemoteHarness harness)
    {
        await harness.SettleUiAsync();
        await harness.Receiver.SettledAsync().WaitAsync(HangGuard, Token);
        await harness.SettleUiAsync();
    }

    // Granary closes: the stop's deadlines run on the test's clock.
    private static async Task StopAsync(RemoteHarness harness)
    {
        var stopping = harness.Receiver.StopAsync();
        harness.Time.Advance(RemoteReceiver.StopBudget);
        await stopping.WaitAsync(HangGuard, Token);
    }

    private static void AssertNothingSecret(Run run)
    {
        string log = run.All;
        Assert.Contains("Connection", log, StringComparison.Ordinal);
        Assert.All(run.NeverLogged, value => Assert.DoesNotContain(value, log, StringComparison.OrdinalIgnoreCase));
        // No frame and no part of one: its text is JSON.
        Assert.DoesNotContain("{", log, StringComparison.Ordinal);
        // The profile's id where a profile is spoken of.
        Assert.DoesNotMatch(ProfileSeven(), log);
    }

    private static void AssertEveryEndLogged(IReadOnlyList<string> lines)
    {
        var reasons = AllReasons();
        var opened = lines.Select(line => Opened().Match(line)).Where(match => match.Success).Select(match => match.Groups[1].Value).ToList();
        var ended = lines.Select(line => Ended().Match(line)).Where(match => match.Success)
            .Select(match => (Number: match.Groups[1].Value, Reason: match.Groups[2].Value.Trim())).ToList();

        Assert.All(opened, number => Assert.Single(ended, end => end.Number == number));
        Assert.All(ended, end => Assert.Contains(end.Reason, reasons));
        // Every number the log names has its end.
        var named = lines.SelectMany(line => Named().Matches(line)).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.All(named, number => Assert.Contains(ended, end => end.Number == number));
    }

    private static HashSet<string> AllReasons() => typeof(RemoteLogReasons).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);

    // The key as hex, base64, base64url and base32, and its public id.
    private static IEnumerable<string> KeyParts(byte[] key)
    {
        string base64 = Convert.ToBase64String(key);
        return [Convert.ToHexString(key), base64, base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'), Base32.Encode(key), FakePhone.KeyIdOf(key)];
    }

    // An exception's message as careless code might write it: the key in every form, a pairing link, the server, a phone, a title.
    private static string Secret(byte[] key) =>
        $"{string.Join(' ', KeyParts(key))} silo://cast?v=2&host=192.168.1.20&port=52817&security=sealed&key={Base32.Encode(key)} "
        + "https://silo.example Elena's phone movie-1 Example Movie";

    // The port is not among them: the receiver logs the port it listens on, which the advertisement names to everyone.
    private static IEnumerable<string> SecretParts(byte[] key) =>
        KeyParts(key).Concat(["silo://cast", "silo.example", "Elena", "movie-1", "Example Movie"]);

    // Both sides' nonces of a phone's handshake, as they were sent and as hex.
    private static void AddNonces(List<string> values, FakePhone phone)
    {
        values.Add(Convert.ToBase64String(phone.Nonce).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        values.Add(Convert.ToHexString(phone.Nonce));
        using var answer = JsonDocument.Parse(phone.ReceivedSealedHello!);
        values.Add(answer.RootElement.GetProperty("nonce").GetString()!);
    }

    [GeneratedRegex(@"profile\W{0,3}7\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileSeven();

    [GeneratedRegex(@"Connection (\d+) opened")]
    private static partial Regex Opened();

    [GeneratedRegex(@"Connection (\d+) (?:closed|refused): (.+)$")]
    private static partial Regex Ended();

    [GeneratedRegex(@"Connection (\d+)\b")]
    private static partial Regex Named();

    [GeneratedRegex(@"(?:closed|refused|stopped): (.+)$")]
    private static partial Regex Reason();

    /// <summary>What one run logged, harness by harness (connection numbers are each receiver's own), and what it met of the never-list.</summary>
    private sealed record Run(IReadOnlyList<IReadOnlyList<string>> Logs, IReadOnlyList<string> NeverLogged)
    {
        public string All => string.Join(Environment.NewLine, Logs.SelectMany(lines => lines));
    }

    /// <summary>An open player that fails as a test says: its command, or the status read after one.</summary>
    private sealed class TroubledPlayer(string secret) : IRemotePlayer
    {
        public bool ApplyThrows { get; set; }

        public bool StateThrows { get; set; }

        public SiloCastState State => StateThrows ? throw new InvalidOperationException(secret) : FakeRemotePlayer.Playing;

        public event EventHandler? StateChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public RemoteCommandResult Apply(SiloCastMessage.Control command) =>
            ApplyThrows ? throw new InvalidOperationException(secret) : RemoteCommandResult.Done;
    }
}
