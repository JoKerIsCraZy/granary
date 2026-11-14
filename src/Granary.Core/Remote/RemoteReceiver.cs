using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Remote;

/// <summary>
/// Granary as a receiver of Silo's phone remote, of the Roku type at level <c>sealed</c>: one per app. It decides who
/// may connect and who is authorized, holds the limits, carries commands to the open player on the UI thread and sends
/// every authorized phone the status (silo-roku's <c>receiver.bs</c>, <c>policy.bs</c> and <c>AppSceneControl.bs</c>).
/// Each phone's connection is a <see cref="RemoteController"/>; nothing here logs a value a phone sent.
/// </summary>
public sealed partial class RemoteReceiver
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly IRemoteListener _listener;
    private readonly IServiceAdvertiser _advertiser;
    private readonly ILanAddressSource _addresses;
    private readonly RemotePairing _pairing;
    private readonly IPlaybackDevicePreferences _preferences;
    private readonly ISessionManager _session;
    private readonly string _name;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<RemoteReceiver> _logger;
    private readonly Lock _gate = new();
    private readonly List<Session> _sessions = [];
    private readonly Dictionary<IPAddress, Queue<long>> _recentByPeer = [];
    private readonly Func<byte[]?> _currentState;
    private int _numbers;

    /// <param name="listener">Listens on the PC's LAN address (the App's <c>TcpListener</c>).</param>
    /// <param name="advertiser">Registers the receiver with DNS-SD (the App's <c>DnsServiceRegister</c>).</param>
    /// <param name="addresses">The PC's LAN address.</param>
    /// <param name="pairing">The receiver's id and its one pairing key.</param>
    /// <param name="preferences">The switch, kept on this PC.</param>
    /// <param name="session">Who is signed in, where: the receiver runs only with a profile.</param>
    /// <param name="client">This PC's name, which the phones show.</param>
    /// <param name="dispatcher">The UI thread: the player is only touched there, and events are raised there.</param>
    /// <param name="time">The clock every deadline runs on.</param>
    /// <param name="logger">Events by connection number and reason only.</param>
    public RemoteReceiver(IRemoteListener listener, IServiceAdvertiser advertiser, ILanAddressSource addresses, RemotePairing pairing,
        IPlaybackDevicePreferences preferences, ISessionManager session, ClientIdentity client, IUiDispatcher dispatcher, TimeProvider time,
        ILogger<RemoteReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(advertiser);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _listener = listener;
        _advertiser = advertiser;
        _addresses = addresses;
        _pairing = pairing;
        _preferences = preferences;
        _session = session;
        _name = client.DeviceName;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _currentState = () => Volatile.Read(ref _stateFrame);
        Identity = () => Volatile.Read(ref _running)?.Identity;
        Pairing = () => _pairing.Current;
    }

    /// <summary>The phones that said hello, in name order, with their names as Granary shows them.</summary>
    public IReadOnlyList<ConnectedPhone> Phones
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Where(IsShownLocked).Select(Shown).OrderBy(phone => phone.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(phone => phone.Number)];
            }
        }
    }

    /// <summary>Raised on the UI thread after a phone came, went, or became active or passive.</summary>
    public event EventHandler? PhonesChanged;

    /// <summary>Raised on the UI thread once for each phone that actively controls, at its hello or when it stops being passive.</summary>
    public event EventHandler<ConnectedPhone>? PhoneConnected;

    /// <summary>What the receiver says about itself at each hello: what it runs as now, or null while it does not run.</summary>
    internal Func<ReceiverIdentity?> Identity { get; set; }

    /// <summary>The pairing material as it is now, read at every handshake and every frame (<see cref="RemotePairing.Current"/>).</summary>
    internal Func<PairingMaterial?> Pairing { get; set; }

    internal RemoteTimings Timings { get; set; } = RemoteTimings.Default;

    /// <summary>
    /// Serves one accepted connection to its end. A peer that is not local, an address that opened more than
    /// <see cref="RemoteReceiverPolicy.MaxNewConnectionsPerMinute"/> connections in the last minute, and a connection
    /// beyond <see cref="RemoteReceiverPolicy.MaxControllers"/> that have not shown the key are closed unread. Every
    /// other one gets its handshake, its place among the phones, the receiver's <c>hello</c>, and then its messages.
    /// </summary>
    public async Task ServeAsync(IRemoteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        int number = Interlocked.Increment(ref _numbers);
        string? refusal = null;
        Session? session = null;
        if (!RemoteReceiverPolicy.IsLocalPeer(connection.Peer))
        {
            refusal = "not a local address";
        }
        else
        {
            lock (_gate)
            {
                if (!TakeConnectionLocked(connection.Peer))
                {
                    refusal = "too many new connections from one address";
                }
                else if (_sessions.Count(other => other.Controller.IsOpen && !other.Controller.IsKeyed) >= RemoteReceiverPolicy.MaxControllers)
                {
                    refusal = "too many connections without the key";
                }
                else
                {
                    var controller = new RemoteController(number, connection, () => Pairing(), Timings, _time, _logger);
                    session = new Session(controller, new CommandBudget(_time));
                    _sessions.Add(session);
                }
            }
        }
        if (session is null)
        {
            _logger.LogInformation("Connection {Number} refused: {Reason}", number, refusal);
            await CloseUnreadAsync(connection).ConfigureAwait(false);
            return;
        }

        try
        {
            await RunAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.Controller.DisposeAsync().ConfigureAwait(false);
            Remove(session);
        }
    }

    /// <summary>Lets the phone on connection <paramref name="number"/> go, with reason <c>tv_disconnected</c> (Disconnect Remote).</summary>
    public async Task DisconnectAsync(int number)
    {
        Session? session;
        lock (_gate)
        {
            session = _sessions.FirstOrDefault(other => other.Controller.Number == number && other.Controller.IsOpen);
        }
        if (session is not null)
        {
            await session.Controller.CloseAsync(SiloCastCloseReasons.TvDisconnected, "disconnected on this PC").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes every connection with <paramref name="reason"/>, each under its close grace, and answers whether none is
    /// left open afterwards.
    /// </summary>
    public Task<bool> CloseAllAsync(string reason) => CloseAllAsync(reason, RemoteLogReasons.Stopped);

    internal async Task<bool> CloseAllAsync(string reason, string logReason)
    {
        List<Session> open;
        lock (_gate)
        {
            open = [.. _sessions.Where(session => session.Controller.IsOpen)];
        }
        await Task.WhenAll(open.Select(session => session.Controller.CloseAsync(reason, logReason))).ConfigureAwait(false);
        lock (_gate)
        {
            return !_sessions.Any(session => session.Controller.IsOpen);
        }
    }

    private async Task RunAsync(Session session, CancellationToken cancellationToken)
    {
        var controller = session.Controller;
        if (!await controller.OpenAsync(cancellationToken).ConfigureAwait(false) || !await AdmitAsync(session).ConfigureAwait(false))
        {
            return;
        }
        if (Identity() is not { } identity)
        {
            await controller.CloseAsync(SiloCastCloseReasons.TvDisconnected, RemoteLogReasons.Stopped).ConfigureAwait(false);
            return;
        }
        // The receiver speaks first once the session is keyed (silo-roku's receiver.bs accepted).
        await controller.SendAsync(SiloCastMessages.Hello(identity)).ConfigureAwait(false);
        await controller.RunAsync(message => OnMessageAsync(session, message), cancellationToken).ConfigureAwait(false);
    }

    // A keyed connection's place among the others, half-open ones counted: below the limit it is let in; at it, one that
    // never said hello, then an unauthorized one, then the oldest passive one makes room; otherwise it is told there are
    // too many and closed.
    private async Task<bool> AdmitAsync(Session session)
    {
        Admission decision;
        Session? evicted = null;
        lock (_gate)
        {
            decision = RemoteReceiverPolicy.Admit([.. OthersLocked(session).Select(Facts)]);
            if (decision is Admission.Evict(int number))
            {
                evicted = _sessions.First(other => other.Controller.Number == number);
                evicted.Leaving = true;
            }
        }
        var controller = session.Controller;
        switch (decision)
        {
            case Admission.Refuse:
                if (Identity() is { } identity)
                {
                    await controller.SendAsync(SiloCastMessages.Hello(identity)).ConfigureAwait(false);
                }
                await controller.SendAsync(SiloCastMessages.Error(SiloCastErrors.TooManyControllers, RemoteTexts.TooManyControllers)).ConfigureAwait(false);
                // The reason goes before the phone listed its features: every phone ignores what it does not read.
                await controller.CloseAsync(SiloCastCloseReasons.TooManyControllers, "too many phones", evenUnlisted: true).ConfigureAwait(false);
                return false;
            case Admission.Evict when evicted is not null:
                _ = evicted.Controller.CloseAsync(SiloCastCloseReasons.TooManyControllers, "made room for another phone");
                return true;
            default:
                return true;
        }
    }

    // Within the minute's window, the connections one address opened; old ones of every address are forgotten, so the
    // table never outgrows the addresses that connected in the last minute.
    private bool TakeConnectionLocked(IPAddress peer)
    {
        var address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        long now = _time.GetTimestamp();
        foreach (var (known, times) in _recentByPeer.ToList())
        {
            while (times.Count > 0 && _time.GetElapsedTime(times.Peek(), now) >= RateWindow)
            {
                times.Dequeue();
            }
            if (times.Count == 0)
            {
                _recentByPeer.Remove(known);
            }
        }
        if (!_recentByPeer.TryGetValue(address, out var recent))
        {
            recent = new Queue<long>();
            _recentByPeer[address] = recent;
        }
        if (recent.Count >= RemoteReceiverPolicy.MaxNewConnectionsPerMinute)
        {
            return false;
        }
        recent.Enqueue(now);
        return true;
    }

    private void Remove(Session session)
    {
        bool shown;
        lock (_gate)
        {
            shown = _sessions.Remove(session) && session.Controller.SaidHello;
        }
        if (shown)
        {
            RaisePhonesChanged();
        }
    }

    private IEnumerable<Session> OthersLocked(Session session) =>
        _sessions.Where(other => !ReferenceEquals(other, session) && other.Controller.IsOpen && !other.Leaving);

    private List<Session> AuthorizedSessions()
    {
        lock (_gate)
        {
            return [.. _sessions.Where(session => session.Controller.IsOpen && session.Controller.IsAuthorized && !session.Leaving)];
        }
    }

    private void RaisePhonesChanged() => _dispatcher.Post(() => PhonesChanged?.Invoke(this, EventArgs.Empty));

    // Once for each phone that actively controls.
    private void Announce(Session session)
    {
        ConnectedPhone phone;
        lock (_gate)
        {
            if (session.Announced)
            {
                return;
            }
            session.Announced = true;
            phone = Shown(session);
        }
        _dispatcher.Post(() => PhoneConnected?.Invoke(this, phone));
    }

    private static bool IsShownLocked(Session session) => session.Controller.SaidHello && session.Controller.IsOpen && !session.Leaving;

    private static ConnectedPhone Shown(Session session) =>
        new(session.Controller.Number, session.Name, session.Controller.IsAuthorized && !session.Controller.IsPassive);

    private static ControllerFacts Facts(Session session)
    {
        var controller = session.Controller;
        return new ControllerFacts(controller.Number, controller.SaidHello, controller.IsAuthorized, controller.IsPassive,
            controller.ConnectedAt, controller.DeviceId);
    }

    private async Task CloseUnreadAsync(IRemoteConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug("A refused connection did not close cleanly ({Error})", RemoteLog.Failure(ex));
        }
    }

    // One connection and what the receiver keeps for it beside the controller's own facts.
    private sealed class Session(RemoteController controller, CommandBudget budget)
    {
        public RemoteController Controller { get; } = controller;

        public CommandBudget Budget { get; } = budget;

        /// <summary>The phone's name as Granary shows it, cleaned at its hello.</summary>
        public string Name { get; set; } = "";

        /// <summary>PhoneConnected was raised for it.</summary>
        public bool Announced { get; set; }

        /// <summary>It is being closed to make room or as replaced, and no longer counts.</summary>
        public bool Leaving { get; set; }
    }
}

/// <summary>A phone connected to the receiver, as the PC shows it.</summary>
/// <param name="Number">The connection's number, to let it go with <see cref="RemoteReceiver.DisconnectAsync"/>.</param>
/// <param name="Name">The phone's name, cleaned.</param>
/// <param name="IsActive">The phone is on Granary's server and actively controls (not passive).</param>
public sealed record ConnectedPhone(int Number, string Name, bool IsActive)
{
    public override string ToString() => $"{nameof(ConnectedPhone)} {Number}";
}
