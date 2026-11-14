using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Granary.Core.Remote;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// One phone's connection: the keyed handshake, frames sealed and in sequence both ways, the heartbeat, the deadlines
/// and the limits, with a <see cref="FakePhone"/> joined in memory and every deadline on a <see cref="ManualTimeProvider"/>.
/// </summary>
public sealed class RemoteControllerTests : IAsyncDisposable
{
    // A small pipe, so a phone that stops reading holds up the receiver's next write at once.
    private const long SmallPipe = 64;

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly RemoteHarness _harness = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ManualTimeProvider Time => _harness.Time;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task APhoneWithTheKey_IsKeyed_AndEveryLaterFrameIsSealed()
    {
        var (controller, phone, _) = _harness.Connect();
        await phone.SendSealedHelloAsync();

        Assert.True(await controller.OpenAsync(Token));

        Assert.True(await phone.ReadSealedHelloAsync());
        using (var document = JsonDocument.Parse(phone.ReceivedSealedHello!))
        {
            var root = document.RootElement;
            Assert.Equal("sealed_hello", root.GetProperty("type").GetString());
            Assert.Equal(1, root.GetProperty("v").GetInt32());
            Assert.Equal(FakePhone.KeyIdOf(RemoteHarness.Key), root.GetProperty("keyId").GetString());
            Assert.Equal(22, root.GetProperty("nonce").GetString()!.Length);
        }
        Assert.True(controller.IsKeyed);

        // From here on the phone opens only what is sealed under the session's keys.
        _ = controller.RunAsync(_ => ValueTask.CompletedTask, Token);
        await controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.PlayerNotReady, "The PC player is not ready yet."));
        await phone.SendAsync("""{"type":"ping","v":2}""");

        Assert.Equal("error", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        Assert.Equal("pong", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        Assert.Equal(0, phone.OpenFailures);
        Assert.Contains("Connection 1 opened", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APhoneWithAnotherKey_GetsAPlainCloseUnauthorized_AndNothingElse()
    {
        var (controller, phone, _) = _harness.Connect(key: RemoteHarness.OtherKey);
        await phone.SendSealedHelloAsync();

        Assert.False(await controller.OpenAsync(Token));

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"unauthorized"}}""", await phone.ReadPlainAsync());
        Assert.Null(await phone.ReadPlainAsync());
        Assert.False(controller.IsKeyed);
        Assert.Contains("Connection 1 closed: wrong pairing key", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APhoneThatDialsPlain_IsRefused()
    {
        var (controller, phone, _) = _harness.Connect();
        await phone.SendPlainAsync("""{"type":"hello","hello":{"role":"phone","deviceName":"Elena's phone","deviceId":"phone-1","supportedVersions":[2]},"v":2}""");

        Assert.False(await controller.OpenAsync(Token));

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"unauthorized"}}""", await phone.ReadPlainAsync());
        Assert.Null(await phone.ReadPlainAsync());
    }

    [Fact]
    public async Task AFirstFrameOver1KiB_ClosesTheConnection_BeforeItsBodyIsRead()
    {
        var (controller, phone, _) = _harness.Connect();
        // Only the length goes: a reader that waited for the body would wait until the deadline.
        await phone.SendLengthAsync(SiloCastFrame.MaxFirstFrameBytes + 1);

        Assert.False(await controller.OpenAsync(Token).WaitAsync(HangGuard, Token));

        Assert.Null(await phone.ReadPlainAsync());
        Assert.Equal(TimeSpan.Zero, Time.Elapsed);
    }

    [Fact]
    public async Task APhoneThatSaysNothingFor5Seconds_IsDropped()
    {
        var (controller, phone, _) = _harness.Connect();
        var opening = controller.OpenAsync(Token);

        Time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.False(opening.IsCompleted);
        Time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(await opening.WaitAsync(HangGuard, Token));
        Assert.Null(await phone.ReadPlainAsync());
        Assert.Contains("closed: no pairing hello in time", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFrameOutOfSequence_ClosesTheConnection_Unanswered()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();

        await phone.SkipASequenceNumberAsync();

        await running.WaitAsync(HangGuard, Token);
        Assert.Null(await phone.ReadAsync());
        Assert.DoesNotContain(phone.Received, message => FakePhone.TypeOf(message) == "pong");
        Assert.Contains("closed: a frame did not open", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplayedFrame_ClosesTheConnection_Unanswered()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();
        await phone.SendAsync("""{"type":"ping","v":2}""");
        Assert.Equal("pong", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));

        await phone.ReplaySealedFrameAsync();

        await running.WaitAsync(HangGuard, Token);
        Assert.Null(await phone.ReadAsync());
        Assert.Single(phone.Received);
    }

    [Fact]
    public async Task ARecordedSession_CannotBeReplayed_OnANewConnection()
    {
        var (_, first, _, _) = await _harness.OpenAsync();
        await first.SendAsync("""{"type":"ping","v":2}""");
        Assert.Equal("pong", FakePhone.TypeOf(await first.ReadAsync() ?? ""));
        var (controller, replayer, _) = _harness.Connect();

        // The same sealed_hello, nonce and all, then the same first frame.
        await replayer.SendPlainAsync(first.SentSealedHello!);
        Assert.True(await controller.OpenAsync(Token));
        Assert.NotNull(await replayer.ReadPlainAsync());
        var running = controller.RunAsync(_ => ValueTask.CompletedTask, Token);
        await replayer.SendRawAsync(first.SentBodies[0]);

        await running.WaitAsync(HangGuard, Token);
        Assert.Null(await replayer.ReadPlainAsync());
        Assert.Contains("Connection 2 closed: a frame did not open", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFrameOver64KiB_ClosesTheConnection()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();

        await phone.SendLengthAsync(SiloCastFrame.MaxFrameBytes + 1);

        await running.WaitAsync(HangGuard, Token);
        Assert.False(controller.IsOpen);
        Assert.Contains("closed: a frame was too large", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FramesLeave_InTheOrderOfTheirSequenceNumbers()
    {
        var (controller, phone, _, _) = await _harness.OpenAsync();
        controller.SaidHello = true;

        await Task.WhenAll(Enumerable.Range(0, 5).Select(sender => Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                await controller.SendAsync(SiloCastMessages.Error($"sender-{sender}", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }, Token)));

        var bySender = new Dictionary<string, List<int>>();
        for (int i = 0; i < 50; i++)
        {
            using var document = JsonDocument.Parse(await phone.ReadAsync() ?? throw new InvalidOperationException("The connection ended."));
            var error = document.RootElement.GetProperty("error");
            string sender = error.GetProperty("code").GetString()!;
            int number = int.Parse(error.GetProperty("message").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            (bySender.TryGetValue(sender, out var numbers) ? numbers : bySender[sender] = []).Add(number);
        }
        Assert.Equal(0, phone.OpenFailures);
        Assert.Equal(5, bySender.Count);
        Assert.All(bySender.Values, numbers => Assert.Equal(Enumerable.Range(0, 10), numbers));
        Assert.True(controller.IsOpen);
    }

    [Fact]
    public async Task APing_IsAnsweredWithAPong()
    {
        var (_, phone, _, _) = await _harness.OpenAsync();

        await phone.SendAsync("""{"type":"ping","v":2}""");

        Assert.Equal("""{"type":"pong","v":2}""", await phone.ReadAsync());
    }

    [Fact]
    public async Task ThePhone_IsPingedEvery3Seconds()
    {
        var (controller, phone, _, _) = await _harness.OpenAsync();
        controller.SaidHello = true;

        Time.Advance(TimeSpan.FromMilliseconds(2999));
        await controller.SendAsync(Marker);
        Assert.Equal(Marker, Encoding.UTF8.GetBytes(await phone.ReadAsync() ?? ""));

        Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("""{"type":"ping","v":2}""", await phone.ReadAsync());
        Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("""{"type":"ping","v":2}""", await phone.ReadAsync());
        Assert.True(controller.IsOpen);
    }

    [Fact]
    public async Task APhoneThatAnswersEveryPing_StaysConnected()
    {
        var (controller, phone, _, _) = await _harness.OpenAsync();
        controller.SaidHello = true;

        for (int beat = 1; beat <= 10; beat++)
        {
            Time.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal("ping", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
            // The pong has been read once the controller answers the phone's own ping after it.
            await phone.SendAsync("""{"type":"ping","v":2}""");
            Assert.Equal("pong", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        }

        Assert.True(controller.IsOpen);
    }

    [Fact]
    public async Task APhoneThatAnswersNoPing_IsDroppedAfterTheFourth()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();
        controller.SaidHello = true;
        phone.AnswersPings = false;

        Time.Advance(TimeSpan.FromMilliseconds(11999));
        Assert.True(controller.IsOpen);
        Time.Advance(TimeSpan.FromMilliseconds(1));

        await running.WaitAsync(HangGuard, Token);
        Assert.Equal(3, phone.Received.Count(message => FakePhone.TypeOf(message) == "ping"));
        Assert.Contains("closed: no answer to pings", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnaskedPong_DoesNotKeepAPhoneConnected()
    {
        // A phone that reads nothing and sends pongs anyway: its pings never leave, so no pong counts.
        var messages = Channel.CreateUnbounded<SiloCastMessage>();
        var (controller, phone, pipe, running) = await _harness.OpenAsync(message =>
        {
            messages.Writer.TryWrite(message);
            return ValueTask.CompletedTask;
        }, SmallPipe);
        controller.SaidHello = true;
        phone.AnswersPings = false;
        pipe.PhoneStopsReading();

        for (int second = 1; second < 12; second++)
        {
            Time.Advance(TimeSpan.FromSeconds(1));
            await phone.SendAsync("""{"type":"pong","v":2}""");
            // Read up to here: the presence after the pong reaches the receiver only once the pong was handled.
            await phone.SendAsync("""{"type":"presence","presence":{"passive":false},"v":2}""");
            Assert.IsType<SiloCastMessage.Presence>(await messages.Reader.ReadAsync(Token).AsTask().WaitAsync(HangGuard, Token));
            Assert.True(controller.IsOpen);
        }
        Time.Advance(TimeSpan.FromSeconds(1));

        await running.WaitAsync(HangGuard, Token);
        Assert.Contains("closed: no answer to pings", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APhoneThatReadsNothingFor10Seconds_IsDropped()
    {
        var (controller, phone, pipe, running) = await _harness.OpenAsync(pauseAt: SmallPipe);
        controller.SaidHello = true;
        pipe.PhoneStopsReading();
        var sending = controller.SendAsync(Marker).AsTask();

        Time.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.True(controller.IsOpen);
        Time.Advance(TimeSpan.FromMilliseconds(1));

        await running.WaitAsync(HangGuard, Token);
        await sending.WaitAsync(HangGuard, Token);
        Assert.Contains("closed: the phone stopped reading", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThan128KiBWaiting_DropsThePhone()
    {
        var (controller, _, pipe, running) = await _harness.OpenAsync(pauseAt: SmallPipe);
        controller.SaidHello = true;
        pipe.PhoneStopsReading();
        byte[] large = SiloCastMessages.Error("large", new string('x', 60 * 1024));

        var sending = Enumerable.Range(0, 3).Select(_ => controller.SendAsync(large).AsTask()).ToList();

        await running.WaitAsync(HangGuard, Token);
        await Task.WhenAll(sending).WaitAsync(HangGuard, Token);
        Assert.Equal(TimeSpan.Zero, Time.Elapsed);
        Assert.Contains("closed: the phone stopped reading", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheNewestWaitingStatus_IsSent()
    {
        var (controller, phone, pipe, _) = await _harness.OpenAsync(pauseAt: SmallPipe);
        controller.SaidHello = true;
        pipe.PhoneStopsReading();
        var blocked = controller.SendAsync(SiloCastMessages.Error("first", "first")).AsTask();
        var made = new List<string>();

        foreach (string title in new[] { "one", "two", "three" })
        {
            controller.OfferState(() =>
            {
                lock (made)
                {
                    made.Add(title);
                }
                return SiloCastMessages.Error("state", title);
            });
        }
        Assert.False(blocked.IsCompleted);
        pipe.PhoneReadsAgain();
        await blocked.WaitAsync(HangGuard, Token);
        await controller.SendAsync(Marker);

        Assert.Contains("\"first\"", await phone.ReadAsync(), StringComparison.Ordinal);
        Assert.Contains("\"three\"", await phone.ReadAsync(), StringComparison.Ordinal);
        Assert.Equal(Marker, Encoding.UTF8.GetBytes(await phone.ReadAsync() ?? ""));
        Assert.Equal(["three"], made);
    }

    [Fact]
    public async Task AClose_CarriesItsReason_OnlyToAPhoneThatListsCloseReason()
    {
        var (listing, listingPhone, _, _) = await _harness.OpenAsync();
        listing.Features = new HashSet<string>([SiloCastFeatures.CloseReason], StringComparer.Ordinal);
        var (silent, silentPhone, _, _) = await _harness.OpenAsync();

        await listing.CloseAsync(SiloCastCloseReasons.TvDisconnected, "the PC let the phone go");
        await silent.CloseAsync(SiloCastCloseReasons.TvDisconnected, "the PC let the phone go");

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await listingPhone.ReadAsync());
        Assert.Null(await listingPhone.ReadAsync());
        Assert.Equal("""{"type":"close","v":2}""", await silentPhone.ReadAsync());
        Assert.Null(await silentPhone.ReadAsync());
        Assert.Contains("Connection 1 closed: the PC let the phone go", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClose_DoesNotWaitLongerThanHalfASecond_ForAPhoneThatIsGone()
    {
        var (controller, _, pipe, _) = await _harness.OpenAsync(pauseAt: SmallPipe);
        controller.SaidHello = true;
        pipe.PhoneStopsReading();
        _ = controller.SendAsync(SiloCastMessages.Error("stuck", "stuck")).AsTask();

        var closing = controller.CloseAsync(SiloCastCloseReasons.TvDisconnected, "the PC let the phone go");
        Time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(closing.IsCompleted);
        Time.Advance(TimeSpan.FromMilliseconds(1));

        await closing.WaitAsync(HangGuard, Token);
        await controller.Ended.WaitAsync(HangGuard, Token);
        Assert.Equal(TimeSpan.FromMilliseconds(500), Time.Elapsed);
    }

    [Fact]
    public async Task APhonesClose_EndsTheConnection()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();

        await phone.SendAsync("""{"type":"close","v":2}""");

        await running.WaitAsync(HangGuard, Token);
        Assert.False(controller.IsOpen);
        Assert.Null(await phone.ReadAsync());
        Assert.Contains("Connection 1 closed: the phone closed", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoHelloWithin5SecondsOfTheKeys_ClosesWithTimeout()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();

        Time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.True(controller.IsOpen);
        Time.Advance(TimeSpan.FromMilliseconds(1));

        await running.WaitAsync(HangGuard, Token);
        // Before a hello the phone has listed no close_reason: a bare close.
        Assert.Equal("""{"type":"close","v":2}""", await phone.NextAsync("close"));
        Assert.Contains("closed: no hello in time", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageThatCannotBeRead_EndsTheConnectionWithABareClose()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();
        controller.Features = new HashSet<string>([SiloCastFeatures.CloseReason], StringComparer.Ordinal);

        await phone.SendAsync("""{"type":"hello","hello":{"role":"phone"},"v":2}""");

        await running.WaitAsync(HangGuard, Token);
        Assert.Equal("""{"type":"close","v":2}""", await phone.ReadAsync());
        Assert.Contains("closed: a message could not be read", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessagesOtherThanTheHeartbeat_GoToTheReceiver_OneAtATime()
    {
        var messages = new List<SiloCastMessage>();
        var (_, phone, _, _) = await _harness.OpenAsync(message =>
        {
            messages.Add(message);
            return ValueTask.CompletedTask;
        });

        await phone.SendAsync("""{"type":"presence","presence":{"passive":true},"v":2}""");
        await phone.SendAsync("""{"type":"surprise","v":2}""");
        await phone.SendAsync("""{"type":"control","control":{"name":"seek","seconds":1798.292724609375},"v":2}""");
        await phone.SendAsync("""{"type":"ping","v":2}""");
        Assert.Equal("pong", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));

        Assert.Collection(messages,
            message => Assert.Equal(new SiloCastMessage.Presence(true), message),
            message => Assert.Equal(1798.292724609375, Assert.IsType<SiloCastMessage.Control>(message).Seconds));
    }

    [Fact]
    public async Task TheSessionKeys_AreGoneAfterTheConnectionEnded()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();
        Assert.True(controller.HoldsSessionKeys);

        await phone.SendAsync("""{"type":"close","v":2}""");
        await running.WaitAsync(HangGuard, Token);

        Assert.False(controller.HoldsSessionKeys);
        await controller.SendAsync(Marker);
        controller.OfferState(() => Marker);
        Assert.Null(await phone.ReadAsync());
    }

    [Fact]
    public async Task AfterAReset_NothingMoreGoesToAPhoneOnTheOldCode_EvenUntold()
    {
        var (controller, phone, _, running) = await _harness.OpenAsync();
        controller.SaidHello = true;

        // The code is reset, and nobody tells the controller.
        _harness.Pairing = new PairingMaterial(RemoteHarness.ReceiverId, RemoteHarness.OtherKey);
        await controller.SendAsync(Marker);

        await running.WaitAsync(HangGuard, Token);
        Assert.Null(await phone.ReadAsync());
        Assert.Empty(phone.Received);
        Assert.Contains("closed: the pairing code was reset", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAReset_APhoneOnTheOldCode_IsDroppedAtItsNextFrame_EvenUntold()
    {
        var (_, phone, _, running) = await _harness.OpenAsync();

        _harness.Pairing = new PairingMaterial(RemoteHarness.ReceiverId, RemoteHarness.OtherKey);
        await phone.SendAsync("""{"type":"ping","v":2}""");

        await running.WaitAsync(HangGuard, Token);
        Assert.Null(await phone.ReadAsync());
        Assert.Empty(phone.Received);
        Assert.Contains("closed: the pairing code was reset", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAResetToAKeyWithTheSameId_APhoneOnTheOldKey_IsDroppedStill()
    {
        var (_, phone, _, running) = await _harness.OpenAsync();
        string keyId = _harness.Pairing!.KeyId;

        // A new key whose id happens to be the old one's: the id is 32 bits, the key is not.
        _harness.Pairing = new PairingMaterial(RemoteHarness.ReceiverId, RemoteHarness.OtherKey, keyId);
        await phone.SendAsync("""{"type":"ping","v":2}""");

        await running.WaitAsync(HangGuard, Token);
        Assert.Empty(phone.Received);
        Assert.Contains("closed: the pairing code was reset", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAReset_AHandshakeWithTheOldCode_IsRefused()
    {
        var (controller, phone, _) = _harness.Connect();
        await phone.SendSealedHelloAsync();
        _harness.Pairing = new PairingMaterial(RemoteHarness.ReceiverId, RemoteHarness.OtherKey);

        Assert.False(await controller.OpenAsync(Token));

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"unauthorized"}}""", await phone.ReadPlainAsync());
    }

    [Fact]
    public async Task TheLog_NamesTheConnectionAndAReason_AndNothingAFrameCarries()
    {
        var (_, phone, _, running) = await _harness.OpenAsync();
        await phone.HelloAsync("c2VydmVy", "deploy-1");
        await phone.SendAsync("""{"type":"close","v":2}""");
        await running.WaitAsync(HangGuard, Token);

        string log = _harness.Logs.All;
        Assert.Contains("Connection 1 opened", log, StringComparison.Ordinal);
        Assert.Contains("Connection 1 closed: the phone closed", log, StringComparison.Ordinal);
        foreach (string secret in new[] { "Elena", FakePhone.DefaultDeviceId, "deploy-1", FakePhone.KeyIdOf(RemoteHarness.Key),
            Convert.ToHexString(RemoteHarness.Key), "{" })
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static byte[] Marker => SiloCastMessages.Error("marker", "marker");
}
