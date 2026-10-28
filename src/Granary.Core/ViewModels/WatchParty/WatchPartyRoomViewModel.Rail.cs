using System.Collections.ObjectModel;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
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
        : IsHost ? "If you drop, the room waits 2 minutes for you before it ends."
        : $"Host: {Room.Host?.DisplayName ?? "unknown"} · {(GuestsCanPause ? "guests can pause" : "host controls playback")}";

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
                    WatchPartyPlaybackStates.Playing => "Watching",
                    WatchPartyPlaybackStates.Waiting => "Buffering",
                    _ => "Paused",
                }
                : member.LobbyReady ? "Ready" : "Connected";
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

    /// <summary>"now", "5m", "2h".</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        double seconds = Math.Max(0, Math.Round((now - at).TotalSeconds));
        if (seconds < 45)
        {
            return "now";
        }
        double minutes = Math.Round(seconds / 60);
        return minutes < 60 ? $"{minutes:0}m" : $"{Math.Round(minutes / 60):0}h";
    }

    private string Line(WatchPartyActivityEntry entry)
    {
        string who = entry.Who ?? "";
        return entry.Kind switch
        {
            WatchPartyActivityKinds.Joined => $"{who} joined",
            WatchPartyActivityKinds.Left => $"{who} left",
            WatchPartyActivityKinds.Ready => $"{who} is ready",
            WatchPartyActivityKinds.Unready => $"{who} is no longer ready",
            WatchPartyActivityKinds.Staged => $"{who} queued {Title(entry.ContentId)}",
            WatchPartyActivityKinds.Unstaged => $"{who} cleared the queue",
            WatchPartyActivityKinds.Started => $"{who} started {Title(entry.ContentId)}",
            WatchPartyActivityKinds.Stopped => $"{who} stopped {Title(entry.ContentId)}",
            WatchPartyActivityKinds.Mode => $"{who} switched the room to {(entry.Detail == WatchPartySelectionModes.Vote ? "everyone votes" : "host picks")}",
            WatchPartyActivityKinds.Policy => $"{who} set {(entry.Detail == WatchPartyPolicies.GuestPlayPause ? "guests can pause" : "host controls playback")}",
            WatchPartyActivityKinds.Playback => $"Playback {(entry.Detail == WatchPartyPlaybackStates.Playing ? "resumed" : "paused")}",
            WatchPartyActivityKinds.Suggested => $"{who} suggested {entry.Detail}",
            WatchPartyActivityKinds.Unsuggested => $"{who} removed {entry.Detail}",
            _ => "",
        };
    }

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
        return "…";
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
                ? $"{series} · S{item.SeasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} E{item.EpisodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}"
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
