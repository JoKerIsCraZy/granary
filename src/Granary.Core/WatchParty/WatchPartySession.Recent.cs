using Granary.Core.Api;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

/// <summary>Where a recent room stands, as the hub shows it.</summary>
public enum WatchPartyRecentState
{
    Live,
    Ended,

    /// <summary>The server could not be asked: the room may still be on.</summary>
    Unknown,

    /// <summary>The proof is refused: the room is not offered again.</summary>
    Gone,
}

// The rooms this profile was in lately, read back for the hub: each is asked once whether it is still on, with the
// proof it kept, as the web's hub does (WatchPartyHub.tsx).
public sealed partial class WatchPartySession
{
    /// <summary>This profile's recent rooms on the active server, newest first.</summary>
    public async Task<IReadOnlyList<WatchPartyRecentRoom>> ListRecentAsync(CancellationToken cancellationToken = default) =>
        _recent is { } recent && _session.ActiveServer?.Id is { } serverId && _session.ProfileId is { } profileId
            ? await recent.ListAsync(serverId, profileId, cancellationToken)
            : [];

    /// <summary>Asks whether a recent room is still on; an ended one is marked so, one whose proof is refused is forgotten.</summary>
    public async Task<WatchPartyRecentState> CheckRecentAsync(WatchPartyRecentRoom room, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        try
        {
            var read = await _api.GetAsync(Server(), room.RoomId, room.Token, cancellationToken);
            if (!read.Room.IsEnded)
            {
                return WatchPartyRecentState.Live;
            }
            await MarkRecentAsync(room, forget: false);
            return WatchPartyRecentState.Ended;
        }
        catch (SiloApiException ex) when (ex.Status == 403)
        {
            await MarkRecentAsync(room, forget: true);
            return WatchPartyRecentState.Gone;
        }
        catch (SiloApiException ex) when (ex.Status is 404 or 409 or 410)
        {
            await MarkRecentAsync(room, forget: false);
            return WatchPartyRecentState.Ended;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogDebug("A recent room could not be checked: {Failure}", ex.GetType().Name);
            return WatchPartyRecentState.Unknown;
        }
    }

    private Task MarkRecentAsync(WatchPartyRecentRoom room, bool forget) => _recent is not { } recent ? Task.CompletedTask
        : forget ? recent.ForgetAsync(room.ServerId, room.ProfileId, room.RoomId)
        : recent.MarkEndedAsync(room.ServerId, room.ProfileId, room.RoomId);
}
