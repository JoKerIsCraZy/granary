using System.Net;
using Granary.Core.Remote;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// What the phone remote's tests share: the pairing material, a clock that moves only when the test moves it, the log,
/// and phones joined to the receiver's side in memory. Nothing here opens a socket.
/// </summary>
internal sealed class RemoteHarness : IAsyncDisposable
{
    public const string ReceiverId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    private readonly List<IAsyncDisposable> _owned = [];
    private readonly List<IDisposable> _pipes = [];
    private int _numbers;

    public RemoteHarness()
    {
        Pairing = new PairingMaterial(ReceiverId, Key);
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

    private RemoteReceiver MakeReceiver() => new(Dispatcher, Time, Logs.For<RemoteReceiver>())
    {
        Identity = () => Identity,
        Pairing = () => Pairing,
    };

    /// <summary>A phone served by the receiver over a new pipe; paired unless <paramref name="pair"/> is false.</summary>
    public async Task<(FakePhone Phone, DuplexPipe Pipe, Task Serving)> ServeAsync(bool pair = true, IPAddress? peer = null,
        long pauseAt = 64 * 1024)
    {
        var pipe = new DuplexPipe(pauseAt);
        _pipes.Add(pipe);
        var phone = new FakePhone(pipe.Phone, Key);
        _owned.Add(phone);
        var serving = Receiver.ServeAsync(pipe.Connection(peer), Token);
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
        if (_receiver is not null)
        {
            await _receiver.CloseAllAsync(SiloCastCloseReasons.TvDisconnected).WaitAsync(TimeSpan.FromSeconds(10));
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
    }
}
