using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

/// <summary>A command of the room, with the connection that delivered it: one from an older connection no longer counts.</summary>
public sealed record WatchPartyDelivery(WatchPartyCommand Command, long Epoch);

/// <summary>
/// The Watch Party this profile is in, for the whole app: the room page and the player both follow it. It joins or
/// opens the room over HTTP, keeps the room socket (<see cref="WatchPartyConnection"/>) while it is in it, and passes on
/// the room's snapshots and commands on the UI thread. Leaving closes the socket (the server has no leave message: a
/// host who leaves ends the room two minutes later unless they come back); the host's End ends it for everyone.
/// </summary>
public sealed partial class WatchPartySession : ObservableObject, IRoomLink
{
    private readonly ISessionManager _session;
    private readonly WatchPartyApi _api;
    private readonly Func<WatchPartyConnection> _connections;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<WatchPartySession> _logger;
    private WatchPartyConnection? _connection;
    private string? _roomToken;
    // The server and profile last seen: a change of either leaves the room.
    private (string? ServerId, string? ProfileId) _seenAs;
    // Counts every entry and leave: an answer to a call made before one is about a room this profile is no longer in.
    private long _generation;

    public WatchPartySession(ISessionManager session, WatchPartyApi api, Func<WatchPartyConnection> connections, IUiDispatcher dispatcher,
        ILogger<WatchPartySession> logger)
    {
        _session = session;
        _api = api;
        _connections = connections;
        _dispatcher = dispatcher;
        _logger = logger;
        _seenAs = (_session.ActiveServer?.Id, _session.ProfileId);
        _session.Changed += (_, _) => _dispatcher.Post(OnSessionChanged);
    }

    /// <summary>The room as last heard of; null while in none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInRoom))]
    public partial WatchPartyRoom? Room { get; private set; }

    [ObservableProperty]
    public partial WatchPartyConnectionState ConnectionState { get; private set; }

    /// <summary>Why the engagement is over (the room ended, or another device took it over); null while it lasts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInRoom))]
    public partial WatchPartyEnd? End { get; private set; }

    public bool IsInRoom => Room is not null && End is null;

    /// <summary>The connection that is open now; commands of an older one no longer count.</summary>
    public long ConnectionEpoch => _connection?.Epoch ?? 0;

    public bool IsConnected => ConnectionState == WatchPartyConnectionState.Connected && End is null;

    /// <summary>Server time minus local time in milliseconds, as the room's pings measured it; null before.</summary>
    public double? ClockOffsetMs => _connection?.Clock.OffsetMs;

    /// <summary>A command of the room, on the UI thread.</summary>
    public event EventHandler<WatchPartyDelivery>? CommandReceived;

    /// <summary>
    /// The playback epoch this device's player last opened for: a room page shown again (after the viewer left the
    /// player) does not open it a second time for the same start.
    /// </summary>
    public long? StartedRevision { get; set; }

    /// <summary>Opens a room this profile hosts and goes in.</summary>
    /// <exception cref="OperationCanceledException">The profile or server changed meanwhile: the room is not entered.</exception>
    public async Task<WatchPartyRoom> CreateAsync(CancellationToken cancellationToken = default)
    {
        var server = Server();
        long generation = _generation;
        var opened = await _api.CreateAsync(server, Guid.NewGuid().ToString(), cancellationToken);
        Stale(generation);
        Enter(server, opened);
        return opened.Room;
    }

    /// <summary>Goes into the room of a code (<c>ABCD1234</c>) or an invitation link of this server.</summary>
    /// <exception cref="ArgumentException">The text is neither.</exception>
    /// <exception cref="OperationCanceledException">The profile or server changed meanwhile: the room is not entered.</exception>
    public async Task<WatchPartyRoom> JoinAsync(string codeOrLink, CancellationToken cancellationToken = default)
    {
        var server = Server();
        var join = ParseJoin(codeOrLink, server) ?? throw new ArgumentException("Enter the room's code or paste its invite link.", nameof(codeOrLink));
        long generation = _generation;
        var joined = await _api.JoinAsync(server, join, cancellationToken);
        Stale(generation);
        Enter(server, joined);
        return joined.Room;
    }

    /// <summary>
    /// A code, or an invitation's token from a link to <paramref name="server"/>'s own Watch Party (<c>…/rooms?token=</c>,
    /// as the web shares it); null for anything else. A token is a credential: one in a link to another host, or to
    /// another page, is not sent anywhere.
    /// </summary>
    public static WatchPartyJoin? ParseJoin(string? text, Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        string input = text?.Trim() ?? "";
        if (input.Contains("token=", StringComparison.OrdinalIgnoreCase))
        {
            return InviteToken(input, server) is { } token ? new WatchPartyJoin { JoinToken = token } : null;
        }
        string code = new([.. input.Where(char.IsAsciiLetterOrDigit)]);
        return code.Length is >= 4 and <= 12 && code.Length == input.Count(c => !char.IsWhiteSpace(c) && c != '-')
            ? new WatchPartyJoin { Code = code.ToUpperInvariant() }
            : null;
    }

    // The token of a link to this server's Watch Party page: same scheme and host (and port), its path the server's own
    // path followed by "/rooms". A path alone ("/rooms?token=…") is taken as the server's.
    private static string? InviteToken(string input, Uri server)
    {
        // A path alone is the room's invite_path, which follows the server's own path as WatchPartyApi.InviteLink puts it.
        string absolute = input.StartsWith('/') && !input.StartsWith("//", StringComparison.Ordinal) ? server.AbsoluteUri.TrimEnd('/') + input : input;
        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var link))
        {
            return null;
        }
        string basePath = server.AbsolutePath.TrimEnd('/');
        if (link.Scheme != server.Scheme || !string.Equals(link.Host, server.Host, StringComparison.OrdinalIgnoreCase) || link.Port != server.Port
            || !string.Equals(link.AbsolutePath.TrimEnd('/'), basePath + "/rooms", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return TokenPattern().Match(link.Query) is { Success: true } match && Uri.UnescapeDataString(match.Groups[1].Value) is { Length: > 0 } token
            ? token
            : null;
    }

    /// <summary>Reads the room again (and renews this profile's proof for it).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Room is not { } room || _roomToken is not { } token)
        {
            return;
        }
        long generation = _generation;
        var read = await _api.GetAsync(Server(), room.RoomId, token, cancellationToken);
        if (generation != _generation || _connection is null)
        {
            return;
        }
        _roomToken = read.RoomAccessToken;
        Show(read.Room);
    }

    /// <summary>The host puts <paramref name="contentId"/> up next in the lobby.</summary>
    public Task StageAsync(string contentId) => HostAsync((server, id) => _api.StageAsync(server, id, contentId));

    /// <summary>The host starts what the lobby has up next, for everyone.</summary>
    public Task StartAsync() => HostAsync((server, id) => _api.StartAsync(server, id));

    /// <summary>The host stops playback for everyone and brings the room back to its lobby.</summary>
    public Task StopAsync() => HostAsync((server, id) => _api.StopAsync(server, id));

    public Task SetGuestsCanPauseAsync(bool allowed) => HostAsync((server, id) => _api.SetPolicyAsync(server, id, allowed));

    /// <summary>The host ends the room for everyone, and leaves it.</summary>
    public async Task EndRoomAsync()
    {
        if (Room is not { } room)
        {
            return;
        }
        await _api.EndAsync(Server(), room.RoomId);
        await LeaveAsync();
    }

    /// <summary>Leaves the room on this device: the socket closes, and nothing of the room stays.</summary>
    public async Task LeaveAsync()
    {
        _generation++;
        var connection = _connection;
        _connection = null;
        _roomToken = null;
        StartedRevision = null;
        Room = null;
        End = null;
        ConnectionState = WatchPartyConnectionState.Disconnected;
        if (connection is not null)
        {
            Detach(connection);
            await connection.DisposeAsync();
        }
    }

    /// <summary>Claims the membership back after another device took it over.</summary>
    public void Rejoin()
    {
        if (Room is not { } room || _roomToken is not { } token || End is not { Replaced: true })
        {
            return;
        }
        End = null;
        Connect(Server(), room.RoomId, token);
    }

    /// <summary>Sends a frame on the room socket; false while it is not connected.</summary>
    public Task<bool> SendAsync(string frame) =>
        _connection is { } connection && IsConnected ? connection.SendAsync(frame) : Task.FromResult(false);

    private async Task HostAsync(Func<Uri, string, Task<WatchPartyRoomResponse>> operation)
    {
        if (Room is not { } room)
        {
            return;
        }
        long generation = _generation;
        var answer = await operation(Server(), room.RoomId);
        // Left (or another profile) meanwhile: the answer is about a room this device is no longer in.
        if (generation != _generation || _connection is null)
        {
            return;
        }
        _roomToken = answer.RoomAccessToken;
        Show(answer.Room);
    }

    private void Stale(long generation)
    {
        if (generation != _generation)
        {
            throw new OperationCanceledException("The profile left or changed while the room was asked for.");
        }
    }

    private void Enter(Uri server, WatchPartyRoomResponse response)
    {
        if (Room is { } current && current.RoomId != response.Room.RoomId)
        {
            // Another room: this one is left first.
            _ = LeaveAsync();
        }
        _generation++;
        _roomToken = response.RoomAccessToken;
        if (Room?.RoomId != response.Room.RoomId)
        {
            StartedRevision = null;
        }
        End = null;
        Room = response.Room;
        Connect(server, response.Room.RoomId, response.RoomAccessToken);
    }

    private void Connect(Uri server, string roomId, string roomToken)
    {
        if (_connection is { } old)
        {
            Detach(old);
            _ = old.DisposeAsync().AsTask();
        }
        var connection = _connections();
        _connection = connection;
        connection.StateChanged += OnConnectionStateChanged;
        connection.FrameReceived += OnFrameReceived;
        connection.Ended += OnEnded;
        connection.Open(server, roomId, roomToken);
    }

    private void Detach(WatchPartyConnection connection)
    {
        connection.StateChanged -= OnConnectionStateChanged;
        connection.FrameReceived -= OnFrameReceived;
        connection.Ended -= OnEnded;
    }

    private void OnConnectionStateChanged(object? sender, WatchPartyConnectionState state) => _dispatcher.Post(() =>
    {
        if (sender == _connection)
        {
            ConnectionState = state;
        }
    });

    private void OnFrameReceived(object? sender, WatchPartyFrame frame)
    {
        long epoch = (sender as WatchPartyConnection)?.Epoch ?? 0;
        _dispatcher.Post(() =>
        {
            if (sender != _connection)
            {
                return;
            }
            switch (frame)
            {
                case WatchPartyFrame.Snapshot snapshot:
                    Show(snapshot.Room);
                    break;
                case WatchPartyFrame.Command command:
                    CommandReceived?.Invoke(this, new WatchPartyDelivery(command.Value, epoch));
                    break;
            }
        });
    }

    private void OnEnded(object? sender, WatchPartyEnd end) => _dispatcher.Post(() =>
    {
        if (sender != _connection)
        {
            return;
        }
        _logger.LogInformation("The Watch Party engagement ended: {Reason}", end.LoggedReason);
        ConnectionState = WatchPartyConnectionState.Disconnected;
        End = end;
    });

    // A snapshot of the same room older than the one shown says nothing new.
    private void Show(WatchPartyRoom room)
    {
        if (Room is { } current && current.RoomId == room.RoomId && current.Generation > room.Generation)
        {
            return;
        }
        Room = room;
        if (room.IsEnded)
        {
            End ??= new WatchPartyEnd("ended");
        }
    }

    // Another server or profile is not in this room, nor in a join or create still on its way.
    private void OnSessionChanged()
    {
        var now = (_session.ActiveServer?.Id, _session.ProfileId);
        if (now == _seenAs)
        {
            return;
        }
        _seenAs = now;
        _generation++;
        if (Room is not null)
        {
            _ = LeaveAsync();
        }
    }

    private Uri Server() => _session is { State: SessionState.Ready, ActiveServer: { } server }
        ? server.BaseUri
        : throw new InvalidOperationException("Sign in to a server to watch together.");

    [GeneratedRegex(@"(?:^|[?&])token=([^&#\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
