using Granary.Core.Abstractions;
using Granary.Core.Api.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

/// <summary>A room this profile was in lately, as the hub offers it back ("Your recent parties").</summary>
/// <param name="Token">The room proof: as secret as a password, kept only in the encrypted secret store.</param>
/// <param name="Title">What the room had up last, when known.</param>
public sealed record WatchPartyRecentRoom(string ServerId, string ProfileId, string RoomId, string Code, string Token, bool IsHost,
    DateTimeOffset LastSeenAt, string? Title = null, bool Ended = false);

/// <summary>
/// The rooms of the last 24 hours, eight at most, as the web keeps them (<c>useRecentRooms.ts</c>): per server and
/// profile, newest first. The web keeps them in the browser's storage; here they go to the DPAPI secret store, since
/// each carries its room proof. A store that cannot be read or written only costs the list.
/// </summary>
public sealed class WatchPartyRecentRooms(ISecretStore secrets, TimeProvider time, ILogger<WatchPartyRecentRooms> logger)
{
    public const string SecretKey = "watch-party.recent-rooms";
    public const int MaxRooms = 8;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>This profile's recent rooms on this server, newest first.</summary>
    public async Task<IReadOnlyList<WatchPartyRecentRoom>> ListAsync(string serverId, string profileId, CancellationToken cancellationToken = default)
    {
        var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(r => r.ServerId == serverId && r.ProfileId == profileId)];
    }

    /// <summary>Puts the room first; a title not given keeps the one it had.</summary>
    public Task RememberAsync(WatchPartyRecentRoom room, CancellationToken cancellationToken = default) => ChangeAsync(rooms =>
    {
        var previous = rooms.Find(r => SameRoom(r, room));
        rooms.RemoveAll(r => SameRoom(r, room));
        rooms.Insert(0, room with { Title = room.Title ?? previous?.Title, Ended = false });
    }, cancellationToken);

    public Task MarkEndedAsync(string serverId, string profileId, string roomId, CancellationToken cancellationToken = default) => ChangeAsync(rooms =>
    {
        int index = rooms.FindIndex(r => r.ServerId == serverId && r.ProfileId == profileId && r.RoomId == roomId);
        if (index >= 0)
        {
            rooms[index] = rooms[index] with { Ended = true };
        }
    }, cancellationToken);

    /// <summary>A room whose proof is refused is not offered again.</summary>
    public Task ForgetAsync(string serverId, string profileId, string roomId, CancellationToken cancellationToken = default) =>
        ChangeAsync(rooms => rooms.RemoveAll(r => r.ServerId == serverId && r.ProfileId == profileId && r.RoomId == roomId), cancellationToken);

    private static bool SameRoom(WatchPartyRecentRoom a, WatchPartyRecentRoom b) =>
        a.ServerId == b.ServerId && a.ProfileId == b.ProfileId && a.RoomId == b.RoomId;

    private async Task ChangeAsync(Action<List<WatchPartyRecentRoom>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rooms = await ReadAsync(cancellationToken).ConfigureAwait(false);
            change(rooms);
            string json = System.Text.Json.JsonSerializer.Serialize([.. rooms.Take(MaxRooms)], WatchPartyJsonContext.Default.ListWatchPartyRecentRoom);
            await secrets.SetAsync(SecretKey, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Only the type: the list holds room proofs.
            logger.LogWarning("The recent Watch Parties could not be saved: {Failure}", ex.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Unreadable or old entries are dropped as they are read.
    private async Task<List<WatchPartyRecentRoom>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await secrets.GetAsync(SecretKey, cancellationToken).ConfigureAwait(false) is not { Length: > 0 } json)
            {
                return [];
            }
            var cutoff = time.GetUtcNow() - MaxAge;
            var rooms = System.Text.Json.JsonSerializer.Deserialize(json, WatchPartyJsonContext.Default.ListWatchPartyRecentRoom) ?? [];
            return [.. rooms.Where(r => r is { RoomId.Length: > 0, Token.Length: > 0 } && r.LastSeenAt > cutoff).OrderByDescending(r => r.LastSeenAt)];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning("The recent Watch Parties could not be read: {Failure}", ex.GetType().Name);
            return [];
        }
    }
}
