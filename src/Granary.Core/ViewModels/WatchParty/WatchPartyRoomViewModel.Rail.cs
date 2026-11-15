using System.Collections.ObjectModel;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

// The rail beside the stage, as the web's (room/RoomRail.tsx): "People" with each member's initials in their colour,
// name, HOST and (you), what they are doing and a dot; "Activity", what this device saw happen, newest first, each with
// how long ago; and the footer (the host's two minutes, or who hosts and who controls playback).
public sealed partial class WatchPartyRoomViewModel
{
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _titleReads = new(StringComparer.Ordinal);
    private DateTimeOffset _activityTickedAt = DateTimeOffset.MinValue;

    public ObservableCollection<WatchPartyMemberRow> Members { get; } = [];

    public ObservableCollection<WatchPartyActivityRow> ActivityRows { get; } = [];

    public int PeopleCount => Room?.MemberCount ?? 0;

    public bool HasNoActivity => ActivityRows.Count == 0;

    public bool HasNoMembers => Members.Count == 0;

    /// <summary>The rail's footer: the host's two minutes, or who hosts and who controls playback.</summary>
    public string RailFooter => Room is null ? ""
        : IsHost ? AppText.Get("web.watchTogether.room.hostFooter")
        : AppText.Get("web.watchTogether.room.guestFooter", ("host", Room.Host?.DisplayName ?? AppText.Get("web.watchTogether.room.unknownHost")),
            ("policy", Policy(GuestsCanPause ? WatchPartyPolicies.GuestPlayPause : WatchPartyPolicies.HostOnly)));

    private void ShowMembers()
    {
        var room = Room;
        var tints = room is null ? new Dictionary<string, int>() : WatchPartyMembers.Tints(room.Members);
        bool playing = room?.IsPlaying == true;
        bool waiting = playing && room!.PlaybackState == WatchPartyPlaybackStates.Waiting;
        Members.Clear();
        foreach (var member in room?.Members ?? [])
        {
            string state = playing
                ? room!.PlaybackState switch
                {
                    WatchPartyPlaybackStates.Playing => AppText.Get("web.watchTogether.rail.stateWatching"),
                    WatchPartyPlaybackStates.Waiting => AppText.Get("web.watchTogether.rail.stateBuffering"),
                    _ => AppText.Get("web.watchTogether.rail.statePaused"),
                }
                : member.LobbyReady ? AppText.Get("web.watchTogether.rail.stateReady") : AppText.Get("web.watchTogether.rail.stateConnected");
            Members.Add(new WatchPartyMemberRow(member.DisplayName, member.IsHost, member.IsSelf, state, !playing && member.LobbyReady, waiting,
                tints.GetValueOrDefault(member.Key)));
        }
        OnPropertyChanged(nameof(HasNoMembers));
    }

    // The session's feed as rows; existing rows keep their text and only their times move on.
    private void ShowActivity()
    {
        var now = _time.GetUtcNow();
        var entries = _party.Activity;
        if (ActivityRows.Count != entries.Count || ActivityRows.Zip(entries).Any(pair => pair.First.Id != pair.Second.Id))
        {
            var known = ActivityRows.ToDictionary(r => r.Id);
            ActivityRows.Clear();
            foreach (var entry in entries)
            {
                var row = known.GetValueOrDefault(entry.Id) ?? new WatchPartyActivityRow(entry.Id, entry.At);
                row.Text = Line(entry);
                row.Time = Ago(row.At, now);
                ActivityRows.Add(row);
            }
            OnPropertyChanged(nameof(HasNoActivity));
        }
        foreach (var row in ActivityRows)
        {
            row.Time = Ago(row.At, now);
        }
        _activityTickedAt = now;
    }

    // The times move on every 30 s, as the web's.
    private void TickActivity()
    {
        if (_time.GetUtcNow() - _activityTickedAt >= TimeSpan.FromSeconds(30))
        {
            ShowActivity();
        }
    }

    /// <summary>"now", "5m", "2h", in the web's words (<c>RoomRail.tsx</c>).</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        double seconds = Math.Max(0, Math.Round((now - at).TotalSeconds));
        if (seconds < 45)
        {
            return AppText.Get("web.watchTogether.activity.timeNow");
        }
        int minutes = (int)Math.Round(seconds / 60);
        return minutes < 60
            ? AppText.Get("web.watchTogether.activity.timeMinutes", ("minutes", minutes))
            : AppText.Get("web.watchTogether.activity.timeHours", ("hours", (int)Math.Round(minutes / 60.0)));
    }

    // Each kind is a whole sentence of the web's, its member and title put in where the language wants them.
    private string Line(WatchPartyActivityEntry entry)
    {
        string who = entry.Who ?? "";
        return entry.Kind switch
        {
            WatchPartyActivityKinds.Joined => AppText.Get("web.watchTogether.activity.joined", ("who", who)),
            WatchPartyActivityKinds.Left => AppText.Get("web.watchTogether.activity.left", ("who", who)),
            WatchPartyActivityKinds.Ready => AppText.Get("web.watchTogether.activity.ready", ("who", who)),
            WatchPartyActivityKinds.Unready => AppText.Get("web.watchTogether.activity.unready", ("who", who)),
            WatchPartyActivityKinds.Staged => AppText.Get("web.watchTogether.activity.staged", ("who", who), ("title", Title(entry.ContentId))),
            WatchPartyActivityKinds.Unstaged => AppText.Get("web.watchTogether.activity.unstaged", ("who", who)),
            WatchPartyActivityKinds.Started => AppText.Get("web.watchTogether.activity.started", ("who", who), ("title", Title(entry.ContentId))),
            WatchPartyActivityKinds.Stopped => AppText.Get("web.watchTogether.activity.stopped", ("who", who), ("title", Title(entry.ContentId))),
            WatchPartyActivityKinds.Mode => AppText.Get("web.watchTogether.activity.mode", ("who", who), ("mode", Mode(entry.Detail))),
            WatchPartyActivityKinds.Policy => AppText.Get("web.watchTogether.activity.policy", ("who", who), ("policy", Policy(entry.Detail))),
            WatchPartyActivityKinds.Playback => entry.Detail == WatchPartyPlaybackStates.Playing
                ? AppText.Get("web.watchTogether.activity.playbackResumed")
                : AppText.Get("web.watchTogether.activity.playbackPaused"),
            // A suggestion whose member left is named as the web names it.
            WatchPartyActivityKinds.Suggested => AppText.Get("web.watchTogether.activity.suggested", ("who", Suggester(entry)), ("title", entry.Detail)),
            WatchPartyActivityKinds.Unsuggested => AppText.Get("web.watchTogether.activity.unsuggested", ("who", Suggester(entry)), ("title", entry.Detail)),
            _ => "",
        };
    }

    private static string Suggester(WatchPartyActivityEntry entry) => entry.Who ?? AppText.Get("web.watchTogether.common.someone");

    // "everyone votes" or "host picks", as the activity names a room's mode.
    private static string Mode(string? mode) => mode == WatchPartySelectionModes.Vote
        ? AppText.Get("web.watchTogether.activity.modeVote")
        : AppText.Get("web.watchTogether.activity.modeHostPick");

    // "guests can pause" or "host controls playback", as the activity and the footer name a policy.
    private static string Policy(string? policy) => policy == WatchPartyPolicies.GuestPlayPause
        ? AppText.Get("web.watchTogether.common.policyGuestPlayPause")
        : AppText.Get("web.watchTogether.common.policyHostOnly");

    // A title by its id: known, or "…" until it is read (the line fills in then).
    private string Title(string? contentId)
    {
        if (contentId is null)
        {
            return "";
        }
        if (_titles.TryGetValue(contentId, out string? title))
        {
            return title;
        }
        if (_titleReads.Add(contentId))
        {
            _ = ReadTitleAsync(contentId);
        }
        return AppText.Get("web.watchTogether.activity.titleLoading");
    }

    private async Task ReadTitleAsync(string contentId)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            var item = await _catalog.GetItemAsync(server.BaseUri, contentId);
            _titles[contentId] = item.Type == CatalogItemTypes.Episode && item.SeriesTitle is { Length: > 0 } series
                ? AppText.Get("web.watchTogether.activity.episodeTitle", ("series", series), ("season", WatchPartyText.Code(item.SeasonNumber)),
                    ("episode", WatchPartyText.Code(item.EpisodeNumber)))
                : item.Title;
            foreach (var row in ActivityRows)
            {
                if (_party.Activity.FirstOrDefault(e => e.Id == row.Id) is { ContentId: { } id } entry && id == contentId)
                {
                    row.Text = Line(entry);
                }
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogDebug("An activity title could not be read: {Failure}", ex.GetType().Name);
        }
    }
}
