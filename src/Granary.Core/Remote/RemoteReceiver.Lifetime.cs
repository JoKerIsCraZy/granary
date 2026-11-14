using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Remote;

// When the receiver exists (silo-roku's ControlTask.bs start and stopAll, AppSceneControl.bs): only while the switch is
// on, a profile is signed in and the PC has a LAN address. One loop owns the lifetime: every trigger only asks it to look
// again, and it compares what is wanted with what runs, so starts and stops never overlap. Starting goes key, listener,
// advertisement, then accepting, because each needs the one before and nothing may name a port before it is bound.
// Stopping goes phones, advertisement, listener, each step by itself so one that fails or hangs skips none of the rest.
public sealed partial class RemoteReceiver
{
    /// <summary>How long <see cref="StopAsync"/> may take, a phone that reads nothing included.</summary>
    public static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long the advertisement waits for the last of quick changes of <c>playing</c> before it is registered anew.</summary>
    public static readonly TimeSpan PlayingSettles = TimeSpan.FromSeconds(2);

    /// <summary>The ports tried in order: Silo's Roku app's 52817 to 52826, then any port Windows gives.</summary>
    public static readonly IReadOnlyList<int> Ports = [.. Enumerable.Range(52817, 10), 0];

    // A stop's share of its budget: the phones, then each of the other two steps.
    private static readonly TimeSpan PhonesBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private readonly Lock _loopGate = new();
    private Task _loop = Task.CompletedTask;
    private bool _loopRunning;
    private bool _lookAgain;
    private bool _started;
    private bool _stopped;

    // What runs, and the connections it takes; null while nothing runs or a start is not finished.
    private Running? _running;
    private CancellationTokenSource? _accepting;
    private RemoteReceiverState _failure = RemoteReceiverState.Listening;
    private RemoteReceiverStatus _status = RemoteReceiverStatus.Off;

    // Whether a title plays now (set on the UI thread) and what the advertisement says once it settled.
    private ITimer? _playingSettle;
    private bool _playingNow;
    private bool _playingSettled;

    /// <summary>Whether the receiver listens, where, and if not, why.</summary>
    public RemoteReceiverStatus Status => Volatile.Read(ref _status);

    /// <summary>Raised on the UI thread when <see cref="Status"/> changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// The link in the pairing code while the receiver listens, with the bound address and port and the key; null
    /// otherwise. It holds the key: it is shown in the code and never logged, saved or copied.
    /// </summary>
    public string? PairingLink => Volatile.Read(ref _running) is { } running
        ? ReceiverAdvertisement.PairingLink(running.Identity, running.Endpoint, running.Material.Key)
        : null;

    /// <summary>Follows the switch, the session, the PC's address and the pairing key from now on.</summary>
    public void Start()
    {
        lock (_loopGate)
        {
            if (_started || _stopped)
            {
                return;
            }
            _started = true;
        }
        _preferences.PhoneRemoteChanged += OnLifetimeChanged;
        _session.Changed += OnLifetimeChanged;
        _addresses.Changed += OnLifetimeChanged;
        _pairing.KeyChanged += OnLifetimeChanged;
        LookAgain();
    }

    /// <summary>
    /// Granary closes: every phone is told, the advertisement removed and the listener closed, within
    /// <see cref="StopBudget"/>. Nothing starts again afterwards.
    /// </summary>
    public async Task StopAsync()
    {
        lock (_loopGate)
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
        }
        _preferences.PhoneRemoteChanged -= OnLifetimeChanged;
        _session.Changed -= OnLifetimeChanged;
        _addresses.Changed -= OnLifetimeChanged;
        _pairing.KeyChanged -= OnLifetimeChanged;
        Volatile.Read(ref _playingSettle)?.Dispose();
        try
        {
            await StopNowAsync().WaitAsync(StopBudget, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The phone remote did not stop within its time");
        }
    }

    /// <summary>
    /// Reset Pairing Code: a new key, every phone on the old one let go, and the receiver started again under the new
    /// key. It does not rely on the news of the reset: it looks again itself, and every handshake and frame checks the
    /// key anyway. The outcome says whether every phone on the old code could be let go.
    /// </summary>
    public async Task<PairingResetOutcome> ResetPairingAsync()
    {
        var before = _pairing.Current;
        try
        {
            await _pairing.ResetKeyAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogError("The pairing code could not be reset ({Error}); the old one stays", RemoteLog.Failure(ex));
            return PairingResetOutcome.Failed;
        }
        LookAgain();
        await SettledAsync().ConfigureAwait(false);
        bool lingering;
        lock (_gate)
        {
            lingering = before is not null && _sessions.Any(session =>
                session.Controller.SessionKeyId == before.KeyId && !session.Controller.Ended.IsCompleted);
        }
        if (lingering)
        {
            _logger.LogWarning("The pairing code was reset, but a phone's connection on the old code could not be closed");
            return PairingResetOutcome.PhonesNotDropped;
        }
        return PairingResetOutcome.Reset;
    }

    /// <summary>Completes once the lifetime has looked at every change made before.</summary>
    internal Task SettledAsync()
    {
        lock (_loopGate)
        {
            return _loop;
        }
    }

    private void OnLifetimeChanged(object? sender, EventArgs e) => LookAgain();

    // Asks the loop to look again; starts it from the UI thread's queue, so changes made in one turn of it are looked at
    // together.
    private void LookAgain()
    {
        TaskCompletionSource loop;
        lock (_loopGate)
        {
            if (_stopped)
            {
                return;
            }
            if (_loopRunning)
            {
                _lookAgain = true;
                return;
            }
            _loopRunning = true;
            loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = loop.Task;
        }
        _dispatcher.Post(() => _ = RunLoopAsync(loop));
    }

    private async Task RunLoopAsync(TaskCompletionSource loop)
    {
        try
        {
            while (true)
            {
                await ReconcileAsync().ConfigureAwait(false);
                lock (_loopGate)
                {
                    if (!_lookAgain)
                    {
                        _loopRunning = false;
                        return;
                    }
                    _lookAgain = false;
                }
            }
        }
        finally
        {
            loop.TrySetResult();
        }
    }

    // Brings what runs to what is wanted.
    private async Task ReconcileAsync()
    {
        await _lifetimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_loopGate)
            {
                if (!_started || _stopped)
                {
                    return;
                }
            }
            var want = Wanted();
            var running = Volatile.Read(ref _running);
            if (running is not null)
            {
                running = WhyStop(running, want) is { } why
                    ? await StopRunningAsync(running, why).ConfigureAwait(false)
                    : await KeepRunningAsync(running, want).ConfigureAwait(false);
            }
            if (running is null && want.State == RemoteReceiverState.Listening)
            {
                await StartRunningAsync(want).ConfigureAwait(false);
            }
            PublishStatus(want);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("The phone remote's lifetime failed ({Error})", RemoteLog.Failure(ex));
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }

    private async Task StopNowAsync()
    {
        await _lifetimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _running) is { } running)
            {
                await StopRunningAsync(running, RemoteLogReasons.Stopped).ConfigureAwait(false);
            }
            PublishStatus(Want.Off);
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }

    // The switch on, a profile signed in, a private IPv4 address: the four conditions, Granary running being the fourth.
    private Want Wanted()
    {
        if (!_preferences.PhoneRemote)
        {
            return Want.Off;
        }
        if (_session.State != SessionState.Ready || _session.ActiveServer is not { } server || _session.ProfileId is null)
        {
            return new Want(RemoteReceiverState.NotSignedIn, null, null);
        }
        if (_addresses.Current is not { } address || !IsPrivateLan(address.Address))
        {
            return new Want(RemoteReceiverState.NoLocalNetwork, server, null);
        }
        return new Want(RemoteReceiverState.Listening, server, address);
    }

    private string? WhyStop(Running running, Want want)
    {
        if (want.State != RemoteReceiverState.Listening)
        {
            return want.State switch
            {
                RemoteReceiverState.Off => "switched off",
                RemoteReceiverState.NotSignedIn => "no profile signed in",
                _ => "no local network",
            };
        }
        if (!string.Equals(running.Server.Id, want.Server!.Id, StringComparison.Ordinal) || running.Server.BaseUri != want.Server.BaseUri)
        {
            return "another server";
        }
        if (!string.Equals(_pairing.Current?.KeyId, running.Material.KeyId, StringComparison.Ordinal))
        {
            return RemoteLogReasons.KeyReset;
        }
        return null;
    }

    // Start order: the key, the listener, the advertisement with the port bound, then accepting. A step that fails leaves
    // nothing listening.
    private async Task StartRunningAsync(Want want)
    {
        _failure = RemoteReceiverState.Listening;
        PairingMaterial material;
        try
        {
            material = await _pairing.LoadOrCreateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogError("The phone remote cannot read its pairing key ({Error}) and does not start", RemoteLog.Failure(ex));
            _failure = RemoteReceiverState.Failed;
            return;
        }

        // Everything that could throw before the listener is bound, so nothing after it leaves a port open.
        var identity = IdentityFor(material, want.Server!);
        var stop = new CancellationTokenSource();
        int? port = await BindAsync(want.Address!, Ports, stop).ConfigureAwait(false);
        if (port is null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            return;
        }
        bool playing = Volatile.Read(ref _playingNow);
        Volatile.Write(ref _playingSettled, playing);
        var running = new Running(want.Server!, want.Address!, port.Value, material, identity, stop, IsAdvertised: false, Playing: playing);
        try
        {
            running = running with { IsAdvertised = await AdvertiseAsync(running, wasAdvertised: false).ConfigureAwait(false) };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("The phone remote's advertisement failed ({Error}); it does not start", RemoteLog.Failure(ex));
            await StopListenerAsync().ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            _failure = RemoteReceiverState.Failed;
            return;
        }
        Volatile.Write(ref _running, running);
        Volatile.Write(ref _accepting, stop);
        _logger.LogInformation("The phone remote listens on port {Port}{Advertised}", running.Port,
            running.IsAdvertised ? "" : ", not advertised");
    }

    // Still wanted: a new address binds there, on the same port when it is free, and a new server name, identity or
    // playing registers anew. Null when it had to stop.
    private async Task<Running?> KeepRunningAsync(Running running, Want want)
    {
        bool rebound = false;
        if (!Equals(running.Address.Address, want.Address!.Address) || running.Address.InterfaceIndex != want.Address.InterfaceIndex)
        {
            Volatile.Write(ref _accepting, null);
            await StopListenerAsync().ConfigureAwait(false);
            int? port = await BindAsync(want.Address, [running.Port, .. Ports.Where(other => other != running.Port)], running.Stop).ConfigureAwait(false);
            if (port is null)
            {
                return await StopRunningAsync(running, "no port at the new address").ConfigureAwait(false);
            }
            running = running with { Address = want.Address, Port = port.Value };
            rebound = true;
        }
        var identity = IdentityFor(running.Material, want.Server!);
        bool playing = Volatile.Read(ref _playingSettled);
        if (rebound || identity != running.Identity || playing != running.Playing)
        {
            var renewed = running with { Identity = identity, Playing = playing };
            try
            {
                renewed = renewed with { IsAdvertised = await AdvertiseAsync(renewed, running.IsAdvertised).ConfigureAwait(false) };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError("The phone remote's advertisement failed ({Error}); it stops", RemoteLog.Failure(ex));
                await StopRunningAsync(running, "the advertisement failed").ConfigureAwait(false);
                _failure = RemoteReceiverState.Failed;
                return null;
            }
            running = renewed;
        }
        Volatile.Write(ref _running, running);
        Volatile.Write(ref _accepting, running.Stop);
        return running;
    }

    // Stop order: the phones, the advertisement, the listener, each bounded and each by itself. Always null.
    private async Task<Running?> StopRunningAsync(Running running, string logReason)
    {
        Volatile.Write(ref _accepting, null);
        Volatile.Write(ref _running, null);
        try
        {
            await CloseAllAsync(SiloCastCloseReasons.TvDisconnected, logReason).WaitAsync(PhonesBudget, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Some phones' connections did not close in time");
        }
        try
        {
            await _advertiser.UnregisterAsync().WaitAsync(StepBudget, _time).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("The phone remote's advertisement could not be removed ({Error})", RemoteLog.Failure(ex));
        }
        await StopListenerAsync().ConfigureAwait(false);
        // Connections still waiting for their pairing hello end here.
        await running.Stop.CancelAsync().ConfigureAwait(false);
        _logger.LogInformation("The phone remote stopped: {Reason}", logReason);
        return null;
    }

    // The port bound, or null with the reason kept for the status.
    private async Task<int?> BindAsync(LanAddress address, IReadOnlyList<int> ports, CancellationTokenSource stop)
    {
        try
        {
            return await _listener.StartAsync(address.Address, ports, connection => AcceptAsync(connection, stop), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("The phone remote found no port to listen on ({Error})", RemoteLog.Failure(ex));
            _failure = RemoteReceiverState.NoPort;
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("The phone remote could not listen ({Error})", RemoteLog.Failure(ex));
            await StopListenerAsync().ConfigureAwait(false);
            _failure = RemoteReceiverState.Failed;
            return null;
        }
    }

    private async Task StopListenerAsync()
    {
        try
        {
            await _listener.StopAsync().WaitAsync(StepBudget, _time).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("The phone remote's listener could not be closed ({Error})", RemoteLog.Failure(ex));
        }
    }

    // True when Windows took the registration. A record that does not fit DNS-SD's strings is not registered, and one made
    // before for other values is removed.
    private async Task<bool> AdvertiseAsync(Running running, bool wasAdvertised)
    {
        var registration = ReceiverAdvertisement.For(running.Identity, running.Endpoint, running.Material.KeyId, running.Playing);
        if (registration is null)
        {
            _logger.LogWarning("The phone remote's record does not fit DNS-SD; phones connect through the pairing code only");
            if (wasAdvertised)
            {
                await _advertiser.UnregisterAsync().WaitAsync(StepBudget, _time).ConfigureAwait(false);
            }
            return false;
        }
        bool taken = await _advertiser.RegisterAsync(registration, CancellationToken.None).ConfigureAwait(false);
        if (!taken)
        {
            _logger.LogWarning("Windows did not advertise the phone remote; phones connect through the pairing code only");
        }
        return taken;
    }

    // A connection the listener accepted: served while the run that bound it accepts, closed unread otherwise.
    private Task AcceptAsync(IRemoteConnection connection, CancellationTokenSource run)
    {
        if (!ReferenceEquals(Volatile.Read(ref _accepting), run) || run.IsCancellationRequested)
        {
            return CloseUnreadAsync(connection);
        }
        return ServeAsync(connection, run.Token);
    }

    private ReceiverIdentity IdentityFor(PairingMaterial material, ServerEntry server) =>
        new(material.ReceiverId, _name, SiloCastServerId.From(server.BaseUri), server.Name, server.ServerId);

    // On the UI thread, from the status: a title that plays changes TXT playing once it has settled for PlayingSettles.
    private void NotePlaying(bool playing)
    {
        if (playing == Volatile.Read(ref _playingNow))
        {
            return;
        }
        Volatile.Write(ref _playingNow, playing);
        var settle = Volatile.Read(ref _playingSettle);
        if (settle is null)
        {
            settle = _time.CreateTimer(static self => ((RemoteReceiver)self!).PlayingSettled(), this, Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            Volatile.Write(ref _playingSettle, settle);
        }
        settle.Change(PlayingSettles, Timeout.InfiniteTimeSpan);
    }

    private void PlayingSettled()
    {
        bool playing = Volatile.Read(ref _playingNow);
        if (playing != Volatile.Read(ref _playingSettled))
        {
            Volatile.Write(ref _playingSettled, playing);
            LookAgain();
        }
    }

    private void PublishStatus(Want want)
    {
        var running = Volatile.Read(ref _running);
        var status = running is not null
            ? new RemoteReceiverStatus(RemoteReceiverState.Listening, running.Address.Address, running.Port, running.IsAdvertised,
                ServerAddress(running.Server))
            : new RemoteReceiverStatus(want.State == RemoteReceiverState.Listening ? _failure : want.State, null, 0, IsAdvertised: false,
                want.Server is { } server ? ServerAddress(server) : null);
        if (status == Volatile.Read(ref _status))
        {
            return;
        }
        Volatile.Write(ref _status, status);
        _dispatcher.Post(() => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    // The address as the viewer typed it, for the pairing panel's hint; never logged.
    private static string ServerAddress(ServerEntry server) => server.BaseUri.AbsoluteUri.TrimEnd('/');

    private static bool IsPrivateLan(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && RemoteReceiverPolicy.IsLocalPeer(address);

    // What is wanted: a state, and for Listening the server and the address.
    private sealed record Want(RemoteReceiverState State, ServerEntry? Server, LanAddress? Address)
    {
        public static Want Off { get; } = new(RemoteReceiverState.Off, null, null);
    }

    // What runs.
    private sealed record Running(
        ServerEntry Server,
        LanAddress Address,
        int Port,
        PairingMaterial Material,
        ReceiverIdentity Identity,
        CancellationTokenSource Stop,
        bool IsAdvertised,
        bool Playing)
    {
        public ReceiverEndpoint Endpoint => new(Address.Address, Port, Address.InterfaceIndex);

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(Running)} on port {Port}");
    }
}

/// <summary>Whether the receiver listens, and if not, why; for the settings row and the Connect a Phone panel.</summary>
/// <param name="State">What the receiver does.</param>
/// <param name="Address">The LAN address it listens on, while it does.</param>
/// <param name="Port">The port bound there, while it listens; else 0.</param>
/// <param name="IsAdvertised">Windows took the DNS-SD registration; without it phones connect through the pairing code only.</param>
/// <param name="ServerAddress">The address of the server Granary is signed in to, for the hint that the phone must use the same.</param>
public sealed record RemoteReceiverStatus(RemoteReceiverState State, IPAddress? Address, int Port, bool IsAdvertised, string? ServerAddress)
{
    public static RemoteReceiverStatus Off { get; } = new(RemoteReceiverState.Off, null, 0, IsAdvertised: false, null);

    public override string ToString() => State.ToString();
}

/// <summary>What the receiver does.</summary>
public enum RemoteReceiverState
{
    /// <summary>The switch is off: nothing listens, nothing is advertised.</summary>
    Off,

    /// <summary>No profile is signed in.</summary>
    NotSignedIn,

    /// <summary>This PC is not on a local network: it has no private IPv4 address.</summary>
    NoLocalNetwork,

    /// <summary>Windows did not give Granary a port.</summary>
    NoPort,

    /// <summary>A step of the start failed (the pairing key could not be read, the advertisement threw): nothing listens.</summary>
    Failed,

    /// <summary>It listens.</summary>
    Listening,
}

/// <summary>How Reset Pairing Code went.</summary>
public enum PairingResetOutcome
{
    /// <summary>A new code, and every phone on the old one was let go.</summary>
    Reset,

    /// <summary>
    /// A new code, but a phone's connection on the old one could not be closed. Nothing more is sent to it or taken from
    /// it, and it is refused at its next frame.
    /// </summary>
    PhonesNotDropped,

    /// <summary>The new code could not be saved: the old one stays.</summary>
    Failed,
}
