using System.Net;
using Granary.Core.Auth;
using Granary.Core.Remote;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// When the receiver exists: only with the switch on, a profile signed in and a LAN address; the listener bound before
/// anything names its port; every stop telling the phones, unregistering and closing the listener, also when a step
/// throws. The listener, the advertiser and the PC's address are fakes that note their calls in order.
/// </summary>
public sealed class RemoteReceiverLifetimeTests : IAsyncDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly RemoteHarness _harness = new(lifetime: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private RemoteReceiver Receiver => _harness.Receiver;

    private ManualTimeProvider Time => _harness.Time;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task WithTheSwitchOff_NothingListens_AndNothingIsAdvertised()
    {
        _harness.Addresses.Set(RemoteHarness.Lan);
        _harness.SignIn();

        Receiver.Start();
        await SettledAsync();

        Assert.Empty(_harness.Calls.All);
        Assert.Equal(RemoteReceiverState.Off, Receiver.Status.State);
        Assert.Null(Receiver.PairingLink);
        // No key is drawn before the switch is turned on.
        Assert.Empty(_harness.Secrets.Values);
    }

    [Fact]
    public async Task SwitchedOnWithAProfile_ItBindsFirst_ThenAdvertisesTheBoundPort()
    {
        _harness.Listener.Binds = _ => 52818;

        await _harness.StartListeningAsync();

        Assert.Equal(["listener.Start(192.168.1.20)", "advertiser.Register(52818, playing=0)"], _harness.Calls.All);
        Assert.Equal(new RemoteReceiverStatus(RemoteReceiverState.Listening, RemoteHarness.Lan.Address, 52818, IsAdvertised: true,
            "https://silo.example"), Receiver.Status);
        var registration = _harness.Advertiser.Current!;
        Assert.Equal(RemoteHarness.Lan.Address, registration.Address);
        Assert.Equal(7u, registration.InterfaceIndex);
        Assert.Equal(_harness.PairingStore.Current!.KeyId, _harness.Advertiser.Txt("kid"));
        Assert.Equal(RemoteHarness.ServerId, _harness.Advertiser.Txt("server"));
        Assert.Equal("Elena's PC", _harness.Advertiser.Txt("name"));
        Assert.Equal(RemoteHarness.ServerIdentity, _harness.Advertiser.Txt("serverIdentity"));
    }

    [Fact]
    public async Task ThePorts_AreSilosRange_ThenAny()
    {
        await _harness.StartListeningAsync();

        int[] expected = [52817, 52818, 52819, 52820, 52821, 52822, 52823, 52824, 52825, 52826, 0];
        Assert.Equal(expected, _harness.Listener.LastPorts);
        Assert.Equal(expected, RemoteReceiver.Ports);
    }

    [Theory]
    [InlineData(SessionState.SignedOut)]
    [InlineData(SessionState.ProfileRequired)]
    [InlineData(SessionState.NoServer)]
    public async Task WithoutAProfile_NothingListens(SessionState state)
    {
        _harness.Addresses.Set(RemoteHarness.Lan);
        _harness.Session.ActiveServer = state == SessionState.NoServer ? null : RemoteHarness.Server();
        _harness.Session.State = state;
        await _harness.Preferences.SetPhoneRemoteAsync(true);

        Receiver.Start();
        await SettledAsync();

        Assert.Empty(_harness.Calls.All);
        Assert.Equal(RemoteReceiverState.NotSignedIn, Receiver.Status.State);
        Assert.Null(Receiver.PairingLink);
    }

    [Fact]
    public async Task WithoutAPrivateAddress_NothingListens_AndTheStatusSaysSo()
    {
        _harness.SignIn();
        await _harness.Preferences.SetPhoneRemoteAsync(true);
        Receiver.Start();
        await SettledAsync();

        Assert.Empty(_harness.Calls.All);
        Assert.Equal(RemoteReceiverState.NoLocalNetwork, Receiver.Status.State);

        _harness.Addresses.Set(RemoteHarness.Lan);
        await SettledAsync();

        Assert.Equal(RemoteReceiverState.Listening, Receiver.Status.State);
    }

    [Fact]
    public async Task WhenNoPortCanBeBound_TheStatusSaysSo_AndNothingIsAdvertised()
    {
        _harness.Listener.NoPortFree = true;

        await _harness.StartListeningAsync();

        Assert.Equal(["listener.Start(192.168.1.20)"], _harness.Calls.All);
        Assert.Equal(RemoteReceiverState.NoPort, Receiver.Status.State);
        Assert.Null(Receiver.PairingLink);
        Assert.Contains("no port", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenWindowsRefusesTheAdvertisement_TheReceiverStillRuns()
    {
        _harness.Advertiser.Accepts = false;

        await _harness.StartListeningAsync();

        Assert.Equal(RemoteReceiverState.Listening, Receiver.Status.State);
        Assert.False(Receiver.Status.IsAdvertised);
        Assert.NotNull(Receiver.PairingLink);
        var (phone, _, _) = await _harness.ConnectThroughListenerAsync();
        Assert.Equal("hello", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
    }

    [Fact]
    public async Task ALongAddressThatDoesNotFitTheTxtRecord_RunsWithoutAdvertising()
    {
        _harness.Addresses.Set(RemoteHarness.Lan);
        _harness.SignIn(RemoteHarness.Server("https://silo.example/" + new string('a', 300)));
        await _harness.Preferences.SetPhoneRemoteAsync(true);

        Receiver.Start();
        await SettledAsync();

        Assert.Equal(["listener.Start(192.168.1.20)"], _harness.Calls.All);
        Assert.Equal(RemoteReceiverState.Listening, Receiver.Status.State);
        Assert.False(Receiver.Status.IsAdvertised);
        Assert.NotNull(Receiver.PairingLink);
    }

    [Fact]
    public async Task SwitchingOff_TellsThePhones_Unregisters_ThenClosesTheListener()
    {
        await _harness.StartListeningAsync();
        var (phone, _, serving) = await _harness.ConnectThroughListenerAsync();
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");

        await _harness.Preferences.SetPhoneRemoteAsync(false);
        await SettledAsync();

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await phone.NextAsync("close"));
        await serving.WaitAsync(HangGuard, Token);
        Assert.Equal(["advertiser.Unregister", "listener.Stop"], _harness.Calls.All.TakeLast(2));
        Assert.False(_harness.Listener.IsListening);
        Assert.Equal(RemoteReceiverState.Off, Receiver.Status.State);
        Assert.Null(Receiver.PairingLink);
    }

    [Fact]
    public async Task SigningOut_StopsIt_AndSigningInAgain_StartsItWithTheSameKey()
    {
        await _harness.StartListeningAsync();
        string kid = _harness.Advertiser.Txt("kid")!;

        _harness.Session.State = SessionState.SignedOut;
        _harness.Session.RaiseChanged();
        await SettledAsync();

        Assert.False(_harness.Listener.IsListening);
        Assert.Null(_harness.Advertiser.Current);
        Assert.Equal(RemoteReceiverState.NotSignedIn, Receiver.Status.State);

        _harness.SignIn();
        await SettledAsync();

        Assert.True(_harness.Listener.IsListening);
        Assert.Equal(kid, _harness.Advertiser.Txt("kid"));
        Assert.Equal(1, _harness.Secrets.Writes);
    }

    [Fact]
    public async Task AnotherServer_StopsIt_AndStartsItForThatServer()
    {
        await _harness.StartListeningAsync();
        string kid = _harness.Advertiser.Txt("kid")!;
        var (phone, _, serving) = await _harness.ConnectThroughListenerAsync();
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");

        _harness.SignIn(RemoteHarness.Server("https://other.example", "entry-2", identity: "deploy-2"));
        await SettledAsync();

        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await phone.NextAsync("close"));
        await serving.WaitAsync(HangGuard, Token);
        string otherId = SiloCastServerId.From(new Uri("https://other.example"));
        Assert.Equal(otherId, _harness.Advertiser.Txt("server"));
        Assert.Equal("deploy-2", _harness.Advertiser.Txt("serverIdentity"));
        Assert.Equal(kid, _harness.Advertiser.Txt("kid"));
        Assert.Contains($"&server={otherId}&", Receiver.PairingLink, StringComparison.Ordinal);
        Assert.Equal("https://other.example", Receiver.Status.ServerAddress);
    }

    [Fact]
    public async Task AnotherProfileOnTheSameServer_KeepsThePhonesConnected()
    {
        await _harness.StartListeningAsync();
        var (phone, _, _) = await _harness.ConnectThroughListenerAsync();
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");
        int calls = _harness.Calls.All.Count;

        _harness.SignIn(profileId: "8");
        await SettledAsync();

        Assert.Equal(calls, _harness.Calls.All.Count);
        await phone.SendAsync("""{"type":"ping","v":2}""");
        Assert.Equal("pong", FakePhone.TypeOf(await phone.NextAsync("pong")));
        Assert.Equal(RemoteReceiverState.Listening, Receiver.Status.State);
    }

    [Fact]
    public async Task ANewAddress_BindsThere_OnTheSamePort_AndAdvertisesAnew()
    {
        _harness.Listener.Binds = _ => 52818;
        await _harness.StartListeningAsync();
        _harness.Listener.Binds = ports => ports[0];
        int before = _harness.Calls.All.Count;

        _harness.Addresses.Set(new LanAddress(IPAddress.Parse("192.168.1.21"), 9));
        await SettledAsync();

        Assert.Equal(["listener.Stop", "listener.Start(192.168.1.21)", "advertiser.Register(52818, playing=0)"], _harness.Calls.All.Skip(before));
        Assert.Equal(52818, _harness.Listener.LastPorts[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.21"), _harness.Advertiser.Current!.Address);
        Assert.Equal(9u, _harness.Advertiser.Current.InterfaceIndex);
        Assert.Contains("host=192.168.1.21&port=52818", Receiver.PairingLink, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALostAddress_StopsIt()
    {
        await _harness.StartListeningAsync();

        _harness.Addresses.Set(null);
        await SettledAsync();

        Assert.False(_harness.Listener.IsListening);
        Assert.Equal(RemoteReceiverState.NoLocalNetwork, Receiver.Status.State);
    }

    [Fact]
    public async Task ATitleOpening_AdvertisesPlaying1_AndClosing_Playing0()
    {
        await _harness.StartListeningAsync();
        var player = new FakeRemotePlayer(_harness.Dispatcher);

        await _harness.AttachAsync(player);
        Time.Advance(RemoteReceiver.PlayingSettles);
        await SettledAsync();

        Assert.Equal(["advertiser.Register(52817, playing=0)", "advertiser.Register(52817, playing=1)"], Registers());

        await _harness.DetachAsync(player);
        Time.Advance(RemoteReceiver.PlayingSettles);
        await SettledAsync();

        Assert.Equal(["advertiser.Register(52817, playing=0)", "advertiser.Register(52817, playing=1)", "advertiser.Register(52817, playing=0)"],
            Registers());
    }

    [Fact]
    public async Task AnIdlePlayer_IsNotPlaying()
    {
        await _harness.StartListeningAsync();
        // A file played directly shows as idle.
        var player = new FakeRemotePlayer(_harness.Dispatcher) { State = SiloCastState.Idle };

        await _harness.AttachAsync(player);
        Time.Advance(RemoteReceiver.PlayingSettles);
        await SettledAsync();

        Assert.Equal(["advertiser.Register(52817, playing=0)"], Registers());
    }

    [Fact]
    public async Task ChangesOfPlayingInQuickSuccession_AreAdvertisedOnce()
    {
        await _harness.StartListeningAsync();
        var player = new FakeRemotePlayer(_harness.Dispatcher);

        await _harness.AttachAsync(player);
        Time.Advance(TimeSpan.FromMilliseconds(500));
        await _harness.DetachAsync(player);
        Time.Advance(TimeSpan.FromMilliseconds(500));
        await _harness.AttachAsync(player);
        Time.Advance(RemoteReceiver.PlayingSettles - TimeSpan.FromMilliseconds(1));
        await SettledAsync();
        Assert.Single(Registers());

        Time.Advance(TimeSpan.FromMilliseconds(1));
        await SettledAsync();

        Assert.Equal(["advertiser.Register(52817, playing=0)", "advertiser.Register(52817, playing=1)"], Registers());
    }

    [Fact]
    public async Task ResetPairing_StopsAndStartsWithANewKid_AndTheOldKeyIsRefused()
    {
        await _harness.StartListeningAsync();
        byte[] oldKey = _harness.PairingStore.Current!.Key.ToArray();
        string oldKid = _harness.Advertiser.Txt("kid")!;
        var (phone, _, serving) = await _harness.ConnectThroughListenerAsync();
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");
        int before = _harness.Calls.All.Count;

        var outcome = await Receiver.ResetPairingAsync().WaitAsync(HangGuard, Token);

        Assert.Equal(PairingResetOutcome.Reset, outcome);
        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"tv_disconnected"}}""", await phone.NextAsync("close"));
        await serving.WaitAsync(HangGuard, Token);
        Assert.Equal(["advertiser.Unregister", "listener.Stop", "listener.Start(192.168.1.20)", "advertiser.Register(52817, playing=0)"],
            _harness.Calls.All.Skip(before));
        Assert.NotEqual(oldKid, _harness.Advertiser.Txt("kid"));
        Assert.Equal(_harness.PairingStore.Current!.KeyId, _harness.Advertiser.Txt("kid"));

        var (stale, _, _) = await _harness.ConnectThroughListenerAsync(oldKey, pair: false);
        await stale.SendSealedHelloAsync();
        Assert.Equal("""{"type":"close","v":2,"close":{"reason":"unauthorized"}}""", await stale.ReadPlainAsync());
        var (fresh, _, _) = await _harness.ConnectThroughListenerAsync();
        Assert.Equal("hello", FakePhone.TypeOf(await fresh.ReadAsync() ?? ""));
    }

    [Fact]
    public async Task AResetWhoseListenerFails_StillLetsEveryPhoneGo()
    {
        await _harness.StartListeningAsync();
        _harness.PairingStore.KeyChanged += (_, _) => throw new InvalidOperationException("listener failed");
        var (phone, _, serving) = await _harness.ConnectThroughListenerAsync();
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");

        var outcome = await Receiver.ResetPairingAsync().WaitAsync(HangGuard, Token);

        Assert.Equal(PairingResetOutcome.Reset, outcome);
        Assert.Equal("close", FakePhone.TypeOf(await phone.NextAsync("close")));
        await serving.WaitAsync(HangGuard, Token);
        Assert.Equal(_harness.PairingStore.Current!.KeyId, _harness.Advertiser.Txt("kid"));
        Assert.Contains("A listener of the pairing code's reset failed (InvalidOperationException", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResetThatCannotLetAPhoneGo_SaysSo()
    {
        await _harness.StartListeningAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (phone, _, _) = await _harness.ConnectThroughListenerAsync(closeWaitsFor: release.Task);
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");

        var resetting = Receiver.ResetPairingAsync();
        await Time.WaitUntilAsync(() => Time.NextDue <= RemoteReceiver.StopBudget, Token);
        Time.Advance(RemoteReceiver.StopBudget);
        var outcome = await resetting.WaitAsync(HangGuard, Token);

        Assert.Equal(PairingResetOutcome.PhonesNotDropped, outcome);
        // Nothing more goes to it, and it was told.
        Assert.Equal("close", FakePhone.TypeOf(await phone.NextAsync("close")));
        Assert.NotEqual(_harness.PairingStore.Current!.KeyId, FakePhone.KeyIdOf(phone.Key));
        release.SetResult();
    }

    [Fact]
    public async Task AResetThatCannotSaveTheNewKey_KeepsTheOldOne_AndSaysSo()
    {
        await _harness.StartListeningAsync();
        string kid = _harness.Advertiser.Txt("kid")!;
        _harness.Secrets.FailWrites = new IOException("The disk is full.");

        var outcome = await Receiver.ResetPairingAsync().WaitAsync(HangGuard, Token);

        Assert.Equal(PairingResetOutcome.Failed, outcome);
        Assert.Equal(kid, _harness.PairingStore.Current!.KeyId);
        Assert.Equal(kid, _harness.Advertiser.Txt("kid"));
        Assert.Contains("IOException", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResetElsewhere_RestartsItUnderTheNewKey()
    {
        await _harness.StartListeningAsync();
        var (phone, _, serving) = await _harness.ConnectThroughListenerAsync();
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");

        var fresh = await _harness.PairingStore.ResetKeyAsync(Token);
        await SettledAsync();

        Assert.Equal("close", FakePhone.TypeOf(await phone.NextAsync("close")));
        await serving.WaitAsync(HangGuard, Token);
        Assert.Equal(fresh.KeyId, _harness.Advertiser.Txt("kid"));
    }

    [Fact]
    public async Task ThePairingLink_ExistsOnlyWhileListening_AndNamesTheBoundAddressAndPort()
    {
        Assert.Null(Receiver.PairingLink);

        await _harness.StartListeningAsync();

        var material = _harness.PairingStore.Current!;
        Assert.Equal(
            $"silo://cast?v=2&host=192.168.1.20&port=52817&id={material.ReceiverId}&name=Elena%27s%20PC&server={RemoteHarness.ServerId}"
            + $"&security=sealed&key={Base32.Encode(material.Key)}",
            Receiver.PairingLink);

        await _harness.Preferences.SetPhoneRemoteAsync(false);
        await SettledAsync();

        Assert.Null(Receiver.PairingLink);
    }

    [Fact]
    public async Task ThePairingLink_NeverShowsTheOldCode_AfterAReset()
    {
        await _harness.StartListeningAsync();
        string oldLink = Receiver.PairingLink!;

        // The reset and a look at the link in one turn of the UI thread, before the receiver could start again.
        var (resetDone, linkMeanwhile) = await _harness.Dispatcher.InvokeAsync(() =>
        {
            var reset = _harness.PairingStore.ResetKeyAsync(Token);
            return (reset.IsCompletedSuccessfully, Receiver.PairingLink);
        });
        Assert.True(resetDone);
        Assert.Null(linkMeanwhile);

        await SettledAsync();
        string newLink = Receiver.PairingLink!;
        Assert.NotEqual(oldLink, newLink);
        Assert.EndsWith($"&key={Base32.Encode(_harness.PairingStore.Current!.Key)}", newLink, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepThatThrows_StillLeavesNothingListening()
    {
        _harness.Advertiser.Throws = new InvalidOperationException("Windows failed.");

        await _harness.StartListeningAsync();

        Assert.Equal(["listener.Start(192.168.1.20)", "advertiser.Register(52817, playing=0)", "listener.Stop"], _harness.Calls.All);
        Assert.False(_harness.Listener.IsListening);
        Assert.Equal(RemoteReceiverState.Failed, Receiver.Status.State);
        Assert.Null(Receiver.PairingLink);
        Assert.Contains("InvalidOperationException", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APairingKeyThatCannotBeRead_LeavesNothingListening_AndSaysSo()
    {
        _harness.Secrets.ReadGate = Task.FromException(new IOException("The store cannot be read."));

        await _harness.StartListeningAsync();

        Assert.Empty(_harness.Calls.All);
        Assert.Equal(RemoteReceiverState.Failed, Receiver.Status.State);
        Assert.Contains("IOException", _harness.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_DoesNotOutlastItsBudget()
    {
        await _harness.StartListeningAsync();
        var (phone, pipe, _) = await _harness.ConnectThroughListenerAsync(pauseAt: 64);
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");
        // A phone that never reads its close, and a removal Windows never confirms.
        pipe.PhoneStopsReading();
        _harness.Advertiser.UnregisterWaitsFor = new TaskCompletionSource().Task;

        var stopping = Receiver.StopAsync();
        await Time.WaitUntilAsync(() => Time.NextDue == RemoteTimings.Default.CloseGrace, Token);
        Time.Advance(RemoteTimings.Default.CloseGrace);
        await Time.WaitUntilAsync(() => _harness.Advertiser.UnregisterStarted, Token);
        Time.Advance(RemoteReceiver.StopBudget - RemoteTimings.Default.CloseGrace);

        await stopping.WaitAsync(HangGuard, Token);
        Assert.False(_harness.Listener.IsListening);
        Assert.Equal("listener.Stop", _harness.Calls.All[^1]);
        Assert.True(Time.Elapsed <= RemoteReceiver.StopBudget);
        Assert.Equal(RemoteReceiverState.Off, Receiver.Status.State);
    }

    [Fact]
    public async Task AfterStopAsync_NothingStartsAgain()
    {
        await _harness.StartListeningAsync();
        await StopAsync();

        await _harness.Preferences.SetPhoneRemoteAsync(false);
        await _harness.Preferences.SetPhoneRemoteAsync(true);
        _harness.Addresses.Set(RemoteHarness.Lan);
        await SettledAsync();

        Assert.Equal(1, _harness.Listener.Starts);
        Assert.False(_harness.Listener.IsListening);
    }

    [Fact]
    public async Task StartsAndStopsInQuickSuccession_EndInTheLastWantedState()
    {
        _harness.Addresses.Set(RemoteHarness.Lan);
        _harness.SignIn();
        Receiver.Start();
        await SettledAsync();

        // On, off and on again within one turn of the UI thread.
        await _harness.Dispatcher.InvokeAsync(() =>
        {
            _ = _harness.Preferences.SetPhoneRemoteAsync(true);
            _ = _harness.Preferences.SetPhoneRemoteAsync(false);
            _ = _harness.Preferences.SetPhoneRemoteAsync(true);
            return true;
        });
        await SettledAsync();

        Assert.Equal(1, _harness.Listener.Starts);
        Assert.Equal(RemoteReceiverState.Listening, Receiver.Status.State);
    }

    [Fact]
    public async Task StatusChanged_IsRaisedOnTheUiThread()
    {
        bool? onUiThread = null;
        Receiver.StatusChanged += (_, _) => onUiThread ??= _harness.Dispatcher.HasThreadAccess;

        await _harness.StartListeningAsync();
        await _harness.SettleUiAsync();

        Assert.True(onUiThread);
    }

    [Fact]
    public async Task TheLog_SaysWhereItListens_ButNoAddressKeyOrName()
    {
        await _harness.StartListeningAsync();
        var (phone, _, _) = await _harness.ConnectThroughListenerAsync();
        await phone.HelloAsync(RemoteHarness.ServerId, null);
        await phone.NextAsync("state");
        await StopAsync();

        string log = _harness.Logs.All;
        Assert.Contains("port 52817", log, StringComparison.Ordinal);
        var material = _harness.PairingStore.Current!;
        foreach (string secret in new[] { "silo.example", RemoteHarness.ServerId, "192.168.1.20", "Elena", "deploy-1", material.KeyId,
            material.ReceiverId, Base32.Encode(material.Key), "silo://" })
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyList<string> Registers() => [.. _harness.Calls.All.Where(call => call.StartsWith("advertiser.Register", StringComparison.Ordinal))];

    private Task SettledAsync() => Receiver.SettledAsync().WaitAsync(HangGuard, Token);

    private async Task StopAsync()
    {
        var stopping = Receiver.StopAsync();
        Time.Advance(RemoteReceiver.StopBudget);
        await stopping.WaitAsync(HangGuard, Token);
    }
}
