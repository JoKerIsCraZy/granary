using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

/// <summary>A command of the room, with the connection that delivered it: one from an older connection no longer counts.</summary>
public sealed record WatchPartyDelivery(WatchPartyCommand Command, long Epoch);

/// <summary>
/// The Watch Party this profile is in, for the whole app: the room page and the player both follow it. It joins or
/// opens the room over HTTP, keeps the room socket (<see cref="WatchPartyConnection"/>) while it is in it, and passes on
/// the room's snapshots, suggestions and commands on the UI thread, with the room's activity as this device saw it.
/// Leaving closes the socket (the server has no leave message: a host who leaves ends the room two minutes later unless
/// they come back); the host's End ends it for everyone. Every room it enters goes to the recent rooms the hub offers back.
/// </summary>
public sealed partial class WatchPartySession : ObservableObject, IRoomLink
{
    private readonly ISessionManager _session;
    private readonly WatchPartyApi _api;
    private readonly Func<WatchPartyConnection> _connections;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<WatchPartySession> _logger;
    private readonly WatchPartyRecentRooms? _recent;
    private readonly TimeProvider _time;
    private readonly WatchPartyActivityFeed _activity = new();
    private WatchPartyConnection? _connection;
    private string? _roomToken;
    // The server and profile last seen: a change of either leaves the room.
    private (string? ServerId, string? ProfileId) _seenAs;
    // Counts every entry and leave: an answer to a call made before one is about a room this profile is no longer in.
    private long _generation;

    public WatchPartySession(ISessionManager session, WatchPartyApi api, Func<WatchPartyConnection> connections, IUiDispatcher dispatcher,
        ILogger<WatchPartySession> logger, WatchPartyRecentRooms? recentRooms = null, TimeProvider? time = null)
    {
        _session = session;
        _api = api;
        _connections = connections;
        _dispatcher = dispatcher;
        _logger = logger;
        _recent = recentRooms;
        _time = time ?? TimeProvider.System;
        _seenAs = (_session.ActiveServer?.Id, _session.ProfileId);
        _session.Changed += (_, _) => _dispatcher.Post(OnSessionChanged);
    }

    /// <summary>The room as last heard of; null while in none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInRoom), nameof(InviteLink))]
    public partial WatchPartyRoom? Room { get; private set; }

    [ObservableProperty]
    public partial WatchPartyConnectionState ConnectionState { get; private set; }

    /// <summary>Why the engagement is over (the room ended, or another device took it over); null while it lasts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInRoom))]
    public partial WatchPartyEnd? End { get; private set; }

    public bool IsInRoom => Room is not null && End is null;

    /// <summary>The room's invitation as a link to the server's web client; null until the room has one.</summary>
    public Uri? InviteLink => Room is { } room && _session.ActiveServer is { } server ? WatchPartyApi.InviteLink(server.BaseUri, room) : null;

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
    /// <exception cref="WatchPartyStaleException">The room was left, or the profile or server changed, meanwhile: it is not entered.</exception>
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
    /// <exception cref="WatchPartyStaleException">The room was left, or the profile or server changed, meanwhile: it is not entered.</exception>
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
    /// A code, or an invitation's token from a link to <paramref name="server"/>'s own Watch Party (the room's
    /// <c>invite_path</c> <c>/rooms/join?token=</c>, or <c>/rooms?token=</c> the web forwards it to); null for anything
    /// else. A token is a credential: one in a link to another host, or to another page, is not sent anywhere.
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

    // The token of a link to this server's Watch Party: same scheme and host (and port), its path the server's own path
    // followed by "/rooms/join" (the invite_path) or "/rooms". A path alone ("/rooms/join?token=…") is taken as the server's.
    private static string? InviteToken(string input, Uri server)
    {
        // A path alone is the room's invite_path, which follows the server's own path as WatchPartyApi.InviteLink puts it.
        string absolute = input.StartsWith('/') && !input.StartsWith("//", StringComparison.Ordinal) ? server.AbsoluteUri.TrimEnd('/') + input : input;
        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var link))
        {
            return null;
        }
        string basePath = server.AbsolutePath.TrimEnd('/');
        string path = link.AbsolutePath.TrimEnd('/');
        if (link.Scheme != server.Scheme || !string.Equals(link.Host, server.Host, StringComparison.OrdinalIgnoreCase) || link.Port != server.Port
            || !(string.Equals(path, basePath + "/rooms/join", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, basePath + "/rooms", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return TokenPattern().Match(link.Query) is { Success: true } match && Uri.UnescapeDataString(match.Groups[1].Value) is { Length: > 0 } token
            ? token
            : null;
    }

    /// <summary>Goes back into a recent room with the proof it kept.</summary>
    /// <exception cref="WatchPartyStaleException">The room was left, or the profile or server changed, meanwhile: it is not entered.</exception>
    public async Task<WatchPartyRoom> ReturnAsync(WatchPartyRecentRoom recent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recent);
        var server = Server();
        long generation = _generation;
        var read = await _api.GetAsync(server, recent.RoomId, recent.Token, cancellationToken);
        Stale(generation);
        if (read.Room.IsEnded)
        {
            throw new SiloApiException(409, ApiProblem.FromStatus(409));
        }
        Enter(server, read);
        return read.Room;
    }

    /// <summary>The title the room has up, for its entry among the recent rooms.</summary>
    public void NoteTitle(string title)
    {
        if (Room is { } room && _roomToken is { } token && title.Length > 0)
        {
            Remember(room, token, title);
        }
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
    /// <returns>True when the room's answer was taken; false when this device was in no room, or left it meanwhile.</returns>
    public Task<bool> StopAsync() => HostAsync((server, id) => _api.StopAsync(server, id));

    /// <returns>True when the room's answer was taken; false when this device was in no room, or left it meanwhile.</returns>
    public Task<bool> SetGuestsCanPauseAsync(bool allowed) => HostAsync((server, id) => _api.SetPolicyAsync(server, id, allowed));

    /// <summary>The host ends the room for everyone, and leaves it.</summary>
    /// <returns>True when a room ended; false when this device was in none.</returns>
    public async Task<bool> EndRoomAsync()
    {
        if (Room is not { } room)
        {
            return false;
        }
        await _api.EndAsync(Server(), room.RoomId);
        MarkEnded(room);
        await LeaveAsync();
        return true;
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
        Suggestions = [];
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

    // True when the answer was taken.
    private async Task<bool> HostAsync(Func<Uri, string, Task<WatchPartyRoomResponse>> operation)
    {
        if (Room is not { } room)
        {
            return false;
        }
        long generation = _generation;
        var answer = await operation(Server(), room.RoomId);
        // Left (or another profile) meanwhile: the answer is about a room this device is no longer in.
        if (generation != _generation || _connection is null)
        {
            return false;
        }
        _roomToken = answer.RoomAccessToken;
        Show(answer.Room);
        return true;
    }

    private void Stale(long generation)
    {
        if (generation != _generation)
        {
            throw new WatchPartyStaleException();
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
            Suggestions = [];
        }
        End = null;
        Room = response.Room;
        Remember(response.Room, response.RoomAccessToken, null);
        Connect(server, response.Room.RoomId, response.RoomAccessToken);
    }

    private void Remember(WatchPartyRoom room, string token, string? title)
    {
        if (_recent is { } recent && _session.ActiveServer?.Id is { } serverId && _session.ProfileId is { } profileId)
        {
            Recent(recent.RememberAsync(new WatchPartyRecentRoom(serverId, profileId, room.RoomId, room.Code, token, room.IsHost, _time.GetUtcNow(),
                title)));
        }
    }

    private void MarkEnded(WatchPartyRoom room)
    {
        if (_recent is { } recent && _session.ActiveServer?.Id is { } serverId && _session.ProfileId is { } profileId)
        {
            Recent(recent.MarkEndedAsync(serverId, profileId, room.RoomId));
        }
    }

    // The recent rooms are kept on the side: a failure is logged (by its type: the list holds proofs), never thrown.
    private void Recent(Task write) => _ = write.ContinueWith(
        t => _logger.LogWarning("The recent Watch Parties could not be updated: {Failure}", t.Exception?.InnerException?.GetType().Name),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // The room is over for good: it ended, closed, or is gone. A refused proof (forbidden) or another device says nothing
    // of the room itself, and the hub asks it again.
    private static bool RoomIsOver(WatchPartyEnd end) => !end.Replaced && end.Reason is "ended" or "room_closed" or "not_found" or "host_left";

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
        if (sender != _connection)
        {
            return;
        }
        bool opened = state == WatchPartyConnectionState.Connected && ConnectionState != WatchPartyConnectionState.Connected;
        ConnectionState = state;
        ObserveActivity();
        if (opened)
        {
            _ = LoadSuggestionsAsync();
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
                case WatchPartyFrame.Suggestions suggestions:
                    TakeSuggestions(suggestions.Items);
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
        if (RoomIsOver(end) && Room is { } room)
        {
            MarkEnded(room);
        }
    });

    // A snapshot of the same room older than the one shown says nothing new.
    private void Show(WatchPartyRoom room)
    {
        if (Room is { } current && current.RoomId == room.RoomId && current.Generation > room.Generation)
        {
            return;
        }
        Room = room;
        if (room.IsEnded && End is null)
        {
            End = new WatchPartyEnd("ended");
            MarkEnded(room);
        }
    }

    partial void OnRoomChanged(WatchPartyRoom? value) => ObserveActivity();

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
