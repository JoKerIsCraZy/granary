using System.Net;
using Granary.Core.Auth;
using Granary.Core.Remote;
using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// What the phone remote's tests share: the pairing material, a clock that moves only when the test moves it, the log,
/// and phones joined to the receiver's side in memory. Nothing here opens a socket or registers a service: the listener,
/// the advertiser and the PC's address are fakes that note their calls in <see cref="Calls"/>.
/// <para>
/// By default the receiver's identity and pairing material are the harness's own (<see cref="Identity"/>,
/// <see cref="Pairing"/>), for tests of what a connection does. With <c>lifetime</c> they are the receiver's own, made
/// from the session, the switch, the address and <see cref="PairingStore"/> once it is started.
/// </para>
/// </summary>
internal sealed class RemoteHarness : IAsyncDisposable
{
    public const string ReceiverId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    /// <summary>The PC's LAN address in the tests.</summary>
    public static readonly LanAddress Lan = new(IPAddress.Parse("192.168.1.20"), 7);

    private readonly List<IAsyncDisposable> _owned = [];
    private readonly List<IDisposable> _pipes = [];
    private readonly bool _lifetime;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private int _numbers;
    private int _peers;

    public RemoteHarness(bool lifetime = false)
    {
        _lifetime = lifetime;
        Pairing = new PairingMaterial(ReceiverId, Key);
        Calls = new RemoteCalls();
        Listener = new FakeRemoteListener(Calls);
        Advertiser = new FakeServiceAdvertiser(Calls);
        PairingStore = new RemotePairing(Secrets, Path.Combine(_directory, "phone-remote.json"), Logs.For<RemotePairing>());
    }

    public RemoteCalls Calls { get; }

    public FakeRemoteListener Listener { get; }

    public FakeServiceAdvertiser Advertiser { get; }

    public FakeLanAddressSource Addresses { get; } = new();

    public InMemoryPlaybackDevicePreferences Preferences { get; } = new();

    public FakeSessionManager Session { get; } = new();

    public InMemorySecretStore Secrets { get; } = new();

    /// <summary>The pairing key's store, which the receiver reads when it runs on its own.</summary>
    public RemotePairing PairingStore { get; }

    public static ClientIdentity Client { get; } = new("device-1", "Elena's PC", ClientIdentity.Platform, "Granary", "0.3.1", "dev",
        ClientIdentity.Family);

    /// <summary>The server Granary is signed in to unless a test says otherwise.</summary>
    public static ServerEntry Server(string address = "https://silo.example", string entryId = "entry-1", string? identity = ServerIdentity) =>
        new(entryId, new Uri(address), "Example Server", identity, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "7");

    /// <summary>Signs in to <paramref name="server"/> with profile <paramref name="profileId"/>, as the session says when it is ready.</summary>
    public void SignIn(ServerEntry? server = null, string profileId = "7")
    {
        Session.ActiveServer = server ?? Server();
        Session.ProfileId = profileId;
        Session.State = SessionState.Ready;
        Session.RaiseChanged();
    }

    /// <summary>The switch on, a profile signed in and the PC on its LAN; then the receiver started and settled.</summary>
    public async Task StartListeningAsync()
    {
        Addresses.Set(Lan);
        SignIn();
        await Preferences.SetPhoneRemoteAsync(true);
        Receiver.Start();
        await Receiver.SettledAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    /// <summary>A phone that connects through the listener with <paramref name="key"/> (the store's key unless given) and pairs.</summary>
    public async Task<(FakePhone Phone, DuplexPipe Pipe, Task Serving)> ConnectThroughListenerAsync(byte[]? key = null, bool pair = true,
        long pauseAt = 64 * 1024, Task? closeWaitsFor = null)
    {
        var pipe = new DuplexPipe(pauseAt);
        _pipes.Add(pipe);
        var phone = new FakePhone(pipe.Phone, key ?? PairingStore.Current!.Key.ToArray());
        _owned.Add(phone);
        var serving = Listener.AcceptAsync(pipe.Connection(NextPeer(), closeWaitsFor));
        if (pair)
        {
            Assert.True(await phone.PairAsync());
        }
        return (phone, pipe, serving);
    }

    /// <summary>The pairing key the phones scanned.</summary>
    public static byte[] Key => Convert.FromHexString("8f3a1c5e9b0d2f4a6c8e0b1d3f5a7c9e");

    /// <summary>A key no phone of the tests scanned.</summary>
    public static byte[] OtherKey => Convert.FromHexString("0f0e0d0c0b0a09080706050403020100");

    /// <summary>The material the receiver holds now; a test that resets the code replaces it.</summary>
    public PairingMaterial? Pairing { get; set; }

    public ManualTimeProvider Time { get; } = new();

    public LogSink Logs { get; } = new();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A controller over a new pipe, and the phone at its other end with <paramref name="key"/> (the paired one unless given).</summary>
    public (RemoteController Controller, FakePhone Phone, DuplexPipe Pipe) Connect(byte[]? key = null, long pauseAt = 64 * 1024,
        IPAddress? peer = null)
    {
        var pipe = new DuplexPipe(pauseAt);
        var controller = new RemoteController(++_numbers, pipe.Connection(peer), () => Pairing, RemoteTimings.Default, Time,
            Logs.For<RemoteController>());
        var phone = new FakePhone(pipe.Phone, key ?? Key);
        _pipes.Add(pipe);
        _owned.Add(controller);
        _owned.Add(phone);
        return (controller, phone, pipe);
    }

    /// <summary>A controller and its phone, keyed, with the controller reading; its messages go to <paramref name="onMessage"/>.</summary>
    public async Task<(RemoteController Controller, FakePhone Phone, DuplexPipe Pipe, Task Running)> OpenAsync(
        Func<SiloCastMessage, ValueTask>? onMessage = null, long pauseAt = 64 * 1024)
    {
        var (controller, phone, pipe) = Connect(pauseAt: pauseAt);
        await phone.SendSealedHelloAsync();
        // Read while it is written: a small pipe holds no whole answer.
        var answer = phone.ReadSealedHelloAsync();
        Assert.True(await controller.OpenAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.True(await answer);
        var running = controller.RunAsync(onMessage ?? (_ => ValueTask.CompletedTask), Token);
        return (controller, phone, pipe, running);
    }

    /// <summary>Each phone on an address of its own, as phones on a LAN are, unless a test gives one.</summary>
    public IPAddress NextPeer() => IPAddress.Parse($"192.168.1.{40 + (Interlocked.Increment(ref _peers) % 200)}");

    /// <summary>The address Granary is signed in to, as its id.</summary>
    public static string ServerId { get; } = SiloCastServerId.From(new Uri("https://silo.example"));

    /// <summary>The deployment identity of <c>https://silo.example</c>.</summary>
    public const string ServerIdentity = "deploy-1";

    /// <summary>What the receiver says about itself.</summary>
    public ReceiverIdentity Identity { get; set; } = new(ReceiverId, "Elena's PC", ServerId, "Example Server", ServerIdentity);

    public SerialDispatcher Dispatcher { get; } = new();

    /// <summary>The receiver, made on first use, with the identity and the material above.</summary>
    public RemoteReceiver Receiver => _receiver ??= MakeReceiver();

    private RemoteReceiver? _receiver;

    private RemoteReceiver MakeReceiver()
    {
        var receiver = new RemoteReceiver(Listener, Advertiser, Addresses, PairingStore, Preferences, Session, Client, Dispatcher, Time,
            Logs.For<RemoteReceiver>());
        if (!_lifetime)
        {
            receiver.Identity = () => Identity;
            receiver.Pairing = () => Pairing;
        }
        return receiver;
    }

    /// <summary>A phone served by the receiver over a new pipe; paired unless <paramref name="pair"/> is false.</summary>
    public async Task<(FakePhone Phone, DuplexPipe Pipe, Task Serving)> ServeAsync(bool pair = true, IPAddress? peer = null,
        long pauseAt = 64 * 1024)
    {
        var pipe = new DuplexPipe(pauseAt);
        _pipes.Add(pipe);
        var phone = new FakePhone(pipe.Phone, Key);
        _owned.Add(phone);
        var serving = Receiver.ServeAsync(pipe.Connection(peer ?? NextPeer()), Token);
        if (pair)
        {
            Assert.True(await phone.PairAsync());
        }
        return (phone, pipe, serving);
    }

    /// <summary>
    /// A phone that paired and said hello on <paramref name="serverId"/> (Granary's unless given), after it read the
    /// receiver's hello.
    /// </summary>
    public async Task<(FakePhone Phone, Task Serving)> HelloAsync(string? serverId = null, string? serverIdentity = null,
        string deviceId = FakePhone.DefaultDeviceId, string deviceName = FakePhone.DefaultDeviceName, bool passive = false)
    {
        var (phone, _, serving) = await ServeAsync();
        Assert.Equal("hello", FakePhone.TypeOf(await phone.ReadAsync() ?? ""));
        await phone.HelloAsync(serverId ?? ServerId, serverIdentity, deviceId, deviceName, passive);
        return (phone, serving);
    }

    /// <summary>Attaches <paramref name="player"/> on the UI thread, as the player page does.</summary>
    public Task AttachAsync(IRemotePlayer player) => Dispatcher.InvokeAsync(() =>
    {
        Receiver.Attach(player);
        return true;
    });

    /// <summary>Detaches <paramref name="player"/> on the UI thread.</summary>
    public Task DetachAsync(IRemotePlayer player) => Dispatcher.InvokeAsync(() =>
    {
        Receiver.Detach(player);
        return true;
    });

    /// <summary>Waits until the UI thread has run what was posted to it so far.</summary>
    public Task SettleUiAsync() => Dispatcher.InvokeAsync(() => true);

    public async ValueTask DisposeAsync()
    {
        if (_receiver is not null && _lifetime)
        {
            // Its deadlines run on the test's clock; a stop that still waits on one after it is left behind.
            var stopping = _receiver.StopAsync();
            Time.Advance(RemoteReceiver.StopBudget);
            await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        }
        foreach (var owned in _owned)
        {
            // Only a hang ends at the guard.
            await owned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        foreach (var pipe in _pipes)
        {
            pipe.Dispose();
        }
        Dispatcher.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
