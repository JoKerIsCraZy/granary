using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

// The stage's titles and the suggestions under it, as the web's stages (room/stage/*.tsx): the hero of what plays, of
// what is up next or of the vote's leader (its headline and "1h 36m · PG · Fantasy" line read from the catalog); the
// suggestions ranked by votes in a vote room, as the guests' ideas in a host-pick lobby, and as what comes after while a
// title plays; and the candidate from the shelf, which the viewer stages or suggests.
public sealed partial class WatchPartyRoomViewModel
{
    private string? _heroShown;
    private CancellationTokenSource? _heroRead;

    public ObservableCollection<WatchPartySuggestionRow> SuggestionRows { get; } = [];

    /// <summary>The hero's title: "Evan Allmächtig (2007)".</summary>
    [ObservableProperty]
    public partial string HeroHeadline { get; private set; } = "";

    /// <summary>"1h 36m · PG · Fantasy, Komödie".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroLine))]
    public partial string HeroMeta { get; private set; } = "";

    [ObservableProperty]
    public partial Uri? HeroPoster { get; private set; }

    [ObservableProperty]
    public partial Uri? HeroBackdrop { get; private set; }

    /// <summary>"1:02:13 · Playing · 3 here" while a title plays.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroLine))]
    public partial string PlayingCaption { get; private set; } = "";

    /// <summary>The vote's leader, as the hero shows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLeader), nameof(HasNoLeader), nameof(LeaderEyebrow), nameof(IsLeading), nameof(PlayLeaderLabel),
        nameof(ShowsHero), nameof(ShowsVotingEmpty), nameof(HeroEyebrow), nameof(HeroTone), nameof(HeroLine))]
    public partial WatchPartySuggestion? Leader { get; private set; }

    /// <summary>The stage's hero shows what plays, what is up next, or the vote's leader.</summary>
    public bool ShowsHero => ShowsPlaying || ShowsStaged || (ShowsVoting && HasLeader);

    /// <summary>"VOTING IS OPEN / No suggestions yet".</summary>
    public bool ShowsVotingEmpty => ShowsVoting && HasNoLeader;

    /// <summary>"NOW PLAYING", "UP NEXT", "FIRST SUGGESTION"…</summary>
    public string HeroEyebrow => (ShowsPlaying ? "Now playing" : ShowsVoting ? LeaderEyebrow : StagedEyebrow).ToUpperInvariant();

    /// <summary><c>live</c> (green) while playing, <c>leading</c> (amber) for a leader with votes, <c>muted</c> otherwise.</summary>
    public string HeroTone => ShowsPlaying ? "live" : ShowsVoting && IsLeading ? "leading" : "muted";

    /// <summary>The hero's facts and what happens next: "1h 36m · PG · Fantasy · Start the leader, or play any suggestion below".</summary>
    public string HeroLine => WatchPartyText.Join(HeroMeta, ShowsPlaying ? PlayingCaption : ShowsVoting ? LeaderCaption : ShowsStaged ? StagedCaption : "");

    public bool HasLeader => Leader is not null;

    public bool HasNoLeader => Leader is null;

    /// <summary>"FIRST SUGGESTION", or "LEADING · 2 OF 3 VOTES" once there are votes.</summary>
    public string LeaderEyebrow => Leader is { VoteCount: > 0 } leader
        ? $"Leading · {leader.VoteCount} of {_party.Suggestions.Sum(s => s.VoteCount)} votes"
        : "First suggestion";

    public bool IsLeading => Leader is { VoteCount: > 0 };

    public string LeaderCaption => IsHost ? "Start the leader, or play any suggestion below" : $"Starts when {HostName} presses play";

    /// <summary>"▶ Play Evan Allmächtig for everyone".</summary>
    public string PlayLeaderLabel => Leader is { } leader ? $"Play {leader.Title} for everyone" : "";

    public string VotingEmptyText =>
        $"Anyone can add one from the shelf below. The room votes, and {(IsHost ? "you start" : $"{HostName} starts")} whatever wins.";

    /// <summary>The suggestions' heading: "SUGGESTIONS · RANKED BY VOTES", "SUGGESTIONS · 3" or "AFTER THIS · STILL OPEN FOR SUGGESTIONS".</summary>
    public string SuggestionsHeading => IsPlaying ? "After this · still open for suggestions"
        : IsVote ? "Suggestions · ranked by votes"
        : $"Suggestions · {_party.Suggestions.Count}";

    public string SuggestionsHint => IsPlaying ? ""
        : IsVote ? "One vote each · change it any time"
        : IsHost ? "Queue one to put it up next." : "The host decides what plays.";

    public bool HasSuggestionsHint => SuggestionsHint.Length > 0;

    /// <summary>The list shows in a vote room and while a title plays; a host-pick lobby shows it once there is one.</summary>
    public bool ShowsSuggestions => Room is not null && !HasCandidate && (IsVote || IsPlaying || _party.Suggestions.Count > 0);

    public bool HasNoSuggestions => SuggestionRows.Count == 0;

    public string SuggestionsEmptyText => IsPlaying
        ? "Nothing queued for after. Browse below to suggest something."
        : "Nothing suggested yet. Browse below to add the first.";

    /// <summary>The host plays the vote's leader for everyone.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task PlayLeaderAsync() => Leader is { } leader ? HostAsync(() => _party.PromoteAsync(leader.Id), "Could not start that") : Task.CompletedTask;

    /// <summary>A vote on or off.</summary>
    [RelayCommand]
    private Task VoteAsync(WatchPartySuggestionRow? row) => row is { CanVote: true }
        ? MemberAsync(() => _party.SetVoteAsync(row.Id, !row.VotedByMe), "Vote failed")
        : Task.CompletedTask;

    /// <summary>The host's "Play this" (in a vote room) or "Queue this" (a host-pick lobby stages it).</summary>
    [RelayCommand]
    private Task PromoteAsync(WatchPartySuggestionRow? row) => row is { CanPromote: true }
        ? IsVote
            ? HostAsync(() => _party.PromoteAsync(row.Id), "Could not start that")
            : HostAsync(() => _party.StageAsync(row.Suggestion.ContentId), "That couldn't be queued")
        : Task.CompletedTask;

    /// <summary>Takes a suggestion back.</summary>
    [RelayCommand]
    private Task DeleteSuggestionAsync(WatchPartySuggestionRow? row) => row is { CanDelete: true }
        ? MemberAsync(() => _party.RemoveSuggestionAsync(row.Id), "Could not remove that")
        : Task.CompletedTask;

    private async Task MemberAsync(Func<Task> action, string failure)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "{Failure}", failure);
            Say(ex is Api.SiloApiException { Problem.Detail: { Length: > 0 } detail } ? detail : failure);
        }
    }

    // The rows as the room's mode and phase show them: votes in a vote room and while playing; the host's queue in a lobby.
    private void ShowSuggestions()
    {
        var room = Room;
        var suggestions = _party.Suggestions;
        bool votes = IsVote || IsPlaying;
        int maxVotes = suggestions.Count > 0 ? suggestions[0].VoteCount : 0;
        var names = (room?.Members ?? []).GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First().DisplayName);
        string? profile = _session.ProfileId;
        var server = _session.ActiveServer?.BaseUri;
        SuggestionRows.Clear();
        for (int i = 0; i < suggestions.Count; i++)
        {
            var suggestion = suggestions[i];
            bool own = suggestion.SuggesterProfileId == profile;
            SuggestionRows.Add(new WatchPartySuggestionRow(suggestion, names.GetValueOrDefault(suggestion.SuggesterKey) ?? "Someone",
                maxVotes > 0 ? (double)suggestion.VoteCount / maxVotes : 0, votes && i == 0 && suggestion.VoteCount > 0, votes, votes,
                IsHost && !IsPlaying, IsVote ? "Play this" : "Queue this", IsHost || own,
                server is null ? null : ArtworkUrl.Resolve(server, suggestion.PosterUrl)));
        }
        Leader = IsVote && suggestions.Count > 0 ? suggestions[0] : null;
        foreach (string name in new[]
        {
            nameof(SuggestionsHeading), nameof(SuggestionsHint), nameof(HasSuggestionsHint), nameof(ShowsSuggestions), nameof(HasNoSuggestions),
            nameof(SuggestionsEmptyText), nameof(LeaderEyebrow), nameof(LeaderCaption), nameof(VotingEmptyText), nameof(PlayLeaderLabel),
        })
        {
            OnPropertyChanged(name);
        }
    }

    // The hero is what plays, what is up next, or the vote's leader.
    private void ShowHero()
    {
        string? content = ShowsVoting ? Leader?.ContentId : Room?.SelectedContentId;
        if (content == _heroShown)
        {
            return;
        }
        _heroShown = content;
        _heroRead?.Cancel();
        HeroHeadline = ShowsVoting && Leader is { } leader ? leader.Title : "";
        HeroMeta = "";
        HeroPoster = ShowsVoting && Leader is { } suggested && _session.ActiveServer is { } at ? ArtworkUrl.Resolve(at.BaseUri, suggested.PosterUrl) : null;
        HeroBackdrop = null;
        if (content is not null)
        {
            _heroRead = new CancellationTokenSource();
            _ = ReadHeroAsync(content, _heroRead.Token);
        }
    }

    private async Task ReadHeroAsync(string content, CancellationToken token)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            var images = await _images.GetAsync(server, ImageSizes.Default, token);
            var item = await _catalog.GetItemAsync(server.BaseUri, content, images: images, cancellationToken: token);
            if (token.IsCancellationRequested || _heroShown != content)
            {
                return;
            }
            HeroHeadline = WatchPartyText.Headline(item);
            HeroMeta = WatchPartyText.Meta(item);
            HeroPoster = ArtworkUrl.Resolve(server.BaseUri, item.PosterUrl);
            HeroBackdrop = ArtworkUrl.Resolve(server.BaseUri, item.BackdropUrl);
            if (content == Room?.SelectedContentId)
            {
                _party.NoteTitle(HeroHeadline);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another title took the stage.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not read the stage's title");
            if (_heroShown == content && HeroHeadline.Length == 0)
            {
                HeroHeadline = "Unavailable";
            }
        }
    }

    // The room's position from its anchor, advancing while it plays, on the server's clock.
    private void ShowPlaying()
    {
        if (Room is not { IsPlaying: true } room)
        {
            PlayingCaption = "";
            return;
        }
        double position = room.AnchorPositionSeconds;
        if (room.PlaybackState == WatchPartyPlaybackStates.Playing && _party.ClockOffsetMs is { } offset)
        {
            position += Math.Max(0, (_time.GetUtcNow().AddMilliseconds(offset) - room.AnchorUpdatedAt).TotalSeconds);
        }
        string state = room.PlaybackState switch
        {
            WatchPartyPlaybackStates.Playing => "Playing",
            WatchPartyPlaybackStates.Waiting => "Waiting for everyone to buffer",
            _ => "Paused",
        };
        PlayingCaption = $"{Clock(position)} · {state} · {room.MemberCount} here";
    }

    private void OnCardChosen(object? sender, WatchPartyShelfCard card)
    {
        bool stages = Shelf.Picks;
        HasCandidate = true;
        Shelf.SelectedId = card.ContentId;
        OnPropertyChanged(nameof(ShowsInvite));
        OnPropertyChanged(nameof(ShowsSuggestions));
        _ = Candidate.ShowAsync(card, stages, stages && IsStaged ? HeroHeadline : null);
    }

    private void DismissCandidate()
    {
        if (!HasCandidate)
        {
            return;
        }
        HasCandidate = false;
        Candidate.Clear();
        Shelf.SelectedId = IsStaged ? Room?.SelectedContentId : null;
        OnPropertyChanged(nameof(ShowsInvite));
        OnPropertyChanged(nameof(ShowsSuggestions));
    }

    private async void OnCandidateConfirmed(object? sender, WatchPartyChoice choice)
    {
        try
        {
            await ConfirmAsync(choice);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            _logger.LogError(ex, "The candidate could not be confirmed");
        }
    }

    /// <summary>Stages the choice (the host of a host-pick lobby) or suggests it, as the shelf's verb was when the card was chosen.</summary>
    public async Task ConfirmAsync(WatchPartyChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Candidate.IsBusy = true;
        try
        {
            if (Candidate.Stages)
            {
                if (await HostAsync(() => _party.StageAsync(choice.ContentId), "That couldn't be queued"))
                {
                    DismissCandidate();
                }
                return;
            }
            await _party.SuggestAsync(new WatchPartySuggestionCreate(Guid.NewGuid().ToString(), choice.ContentId, choice.ContentType, choice.Title,
                choice.Subtitle, choice.PosterPath));
            Say($"Suggested {choice.Title}");
            DismissCandidate();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not suggest a title");
            Say(ex is Api.SiloApiException { Problem.Detail: { Length: > 0 } detail } ? detail : "Could not suggest that");
        }
        finally
        {
            Candidate.IsBusy = false;
        }
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
