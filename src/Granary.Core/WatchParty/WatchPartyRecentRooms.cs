using System.Text;
using Granary.Core.Abstractions;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

/// <summary>A room this profile was in lately, as the hub offers it back ("Your recent parties").</summary>
/// <param name="Token">The room proof: as secret as a password, kept only in the encrypted secret store, blank once the room ended.</param>
/// <param name="Title">What the room had up last, when known.</param>
public sealed record WatchPartyRecentRoom(string ServerId, string ProfileId, string RoomId, string Code, string Token, bool IsHost,
    DateTimeOffset LastSeenAt, string? Title = null, bool Ended = false)
{
    // The proof never goes into a log line or a debugger's view by way of ToString.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"ServerId = {ServerId}, ProfileId = {ProfileId}, RoomId = {RoomId}, IsHost = {IsHost}, LastSeenAt = {LastSeenAt:O}, ");
        builder.Append($"Title = {Title}, Ended = {Ended}");
        return true;
    }
}

/// <summary>
/// The rooms of the last 24 hours, eight at most per server and profile, as the web keeps them (<c>useRecentRooms.ts</c>),
/// newest first. The web keeps them in the browser's storage; here they go to the DPAPI secret store, since each carries
/// its room proof. A room that ended keeps no proof, expired rooms are dropped from the store as soon as they are read,
/// and signing out of a server or removing it takes its rooms with it. A store that cannot be read or written only
/// costs the list.
/// </summary>
public sealed class WatchPartyRecentRooms
{
    public const string SecretKey = "watch-party.recent-rooms";
    public const int MaxRooms = 8;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<WatchPartyRecentRooms> _logger;
    private readonly ICredentialStore? _credentials;
    private readonly IServerRegistry? _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WatchPartyRecentRooms(ISecretStore secrets, TimeProvider time, ILogger<WatchPartyRecentRooms> logger, ICredentialStore? credentials = null,
        IServerRegistry? registry = null)
    {
        _secrets = secrets;
        _time = time;
        _logger = logger;
        _credentials = credentials;
        _registry = registry;
        if (credentials is not null)
        {
            credentials.Changed += OnCredentialsChanged;
        }
        if (registry is not null)
        {
            registry.Changed += OnRegistryChanged;
        }
    }

    /// <summary>This profile's recent rooms on this server, newest first.</summary>
    public async Task<IReadOnlyList<WatchPartyRecentRoom>> ListAsync(string serverId, string profileId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WatchPartyRecentRoom> all = [];
        await ChangeAsync(rooms => all = [.. rooms], cancellationToken);
        return [.. all.Where(r => r.ServerId == serverId && r.ProfileId == profileId)];
    }

    /// <summary>Puts the room first; a title not given keeps the one it had.</summary>
    public Task RememberAsync(WatchPartyRecentRoom room, CancellationToken cancellationToken = default) => ChangeAsync(rooms =>
    {
        var previous = rooms.Find(r => SameRoom(r, room));
        rooms.RemoveAll(r => SameRoom(r, room));
        rooms.Insert(0, room with { Title = room.Title ?? previous?.Title, Ended = false });
    }, cancellationToken);

    /// <summary>The room ended: it stays on the list as ended, and its proof goes.</summary>
    public Task MarkEndedAsync(string serverId, string profileId, string roomId, CancellationToken cancellationToken = default) => ChangeAsync(rooms =>
    {
        int index = rooms.FindIndex(r => r.ServerId == serverId && r.ProfileId == profileId && r.RoomId == roomId);
        if (index >= 0)
        {
            rooms[index] = rooms[index] with { Ended = true, Token = "" };
        }
    }, cancellationToken);

    /// <summary>A room whose proof is refused is not offered again.</summary>
    public Task ForgetAsync(string serverId, string profileId, string roomId, CancellationToken cancellationToken = default) =>
        ChangeAsync(rooms => rooms.RemoveAll(r => r.ServerId == serverId && r.ProfileId == profileId && r.RoomId == roomId), cancellationToken);

    /// <summary>Every room of a server (signed out of, or removed), or of one of its profiles, with its proof.</summary>
    public Task ClearAsync(string serverId, string? profileId = null, CancellationToken cancellationToken = default) =>
        ChangeAsync(rooms => rooms.RemoveAll(r => r.ServerId == serverId && (profileId is null || r.ProfileId == profileId)), cancellationToken);

    private static bool SameRoom(WatchPartyRecentRoom a, WatchPartyRecentRoom b) =>
        a.ServerId == b.ServerId && a.ProfileId == b.ProfileId && a.RoomId == b.RoomId;

    // Signed out: the server's credentials are gone, and so are its rooms' proofs.
    private void OnCredentialsChanged(object? sender, string serverEntryId) => Run(PurgeSignedOutAsync(serverEntryId), "after a sign-out");

    private async Task PurgeSignedOutAsync(string serverEntryId)
    {
        if (_credentials is { } credentials && await credentials.GetAsync(serverEntryId).ConfigureAwait(false) is null)
        {
            await ClearAsync(serverEntryId).ConfigureAwait(false);
        }
    }

    // A removed server takes its rooms along.
    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        if (_registry is not { } registry)
        {
            return;
        }
        var saved = registry.Servers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Run(ChangeAsync(rooms => rooms.RemoveAll(r => !saved.Contains(r.ServerId)), CancellationToken.None), "after a server was removed");
    }

    private void Run(Task task, string when) => _ = task.ContinueWith(
        t => _logger.LogWarning("The recent Watch Parties could not be cleared {When}: {Failure}", when, t.Exception?.InnerException?.GetType().Name),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // Read, change and write back under one gate; what the read dropped (expired, unreadable) is written away too.
    private async Task ChangeAsync(Action<List<WatchPartyRecentRoom>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (rooms, dropped) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            string before = Serialize(rooms);
            change(rooms);
            var kept = Capped(rooms);
            string after = Serialize(kept);
            if (dropped || after != before)
            {
                await _secrets.SetAsync(SecretKey, after, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Only the type: the list holds room proofs.
            _logger.LogWarning("The recent Watch Parties could not be saved: {Failure}", ex.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Eight per server and profile, newest first.
    private static List<WatchPartyRecentRoom> Capped(List<WatchPartyRecentRoom> rooms) =>
        [.. rooms.GroupBy(r => (r.ServerId, r.ProfileId)).SelectMany(g => g.Take(MaxRooms)).OrderByDescending(r => r.LastSeenAt)];

    private static string Serialize(List<WatchPartyRecentRoom> rooms) =>
        System.Text.Json.JsonSerializer.Serialize(rooms, WatchPartyJsonContext.Default.ListWatchPartyRecentRoom);

    // Expired, unusable or unreadable entries are dropped as they are read; true when anything was.
    private async Task<(List<WatchPartyRecentRoom> Rooms, bool Dropped)> ReadAsync(CancellationToken cancellationToken)
    {
        string? json;
        try
        {
            json = await _secrets.GetAsync(SecretKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning("The recent Watch Parties could not be read: {Failure}", ex.GetType().Name);
            return ([], false);
        }
        if (json is not { Length: > 0 })
        {
            return ([], false);
        }
        List<WatchPartyRecentRoom> stored;
        try
        {
            stored = System.Text.Json.JsonSerializer.Deserialize(json, WatchPartyJsonContext.Default.ListWatchPartyRecentRoom) ?? [];
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning("The recent Watch Parties could not be read: {Failure}", ex.GetType().Name);
            return ([], true);
        }
        var cutoff = _time.GetUtcNow() - MaxAge;
        var rooms = stored.Where(r => r is { RoomId.Length: > 0 } && (r.Token.Length > 0 || r.Ended) && r.LastSeenAt > cutoff)
            .OrderByDescending(r => r.LastSeenAt).ToList();
        return (rooms, rooms.Count != stored.Count);
    }
}
