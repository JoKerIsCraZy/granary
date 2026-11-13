using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Texts;
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
    private IReadOnlyList<WatchPartySuggestion>? _rowsFrom;
    private string _rowsKey = "";
    // The suggestion a confirm sends, kept for its candidate: a retry after a lost answer is the same suggestion.
    private (string ContentId, string SuggestionId)? _suggestion;

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
    public string HeroEyebrow => AppText.Upper(ShowsPlaying ? AppText.Get("web.watchTogether.playing.eyebrow") : ShowsVoting ? LeaderEyebrow : StagedEyebrow);

    /// <summary><c>live</c> (green) while playing, <c>leading</c> (amber) for a leader with votes, <c>muted</c> otherwise.</summary>
    public string HeroTone => ShowsPlaying ? "live" : ShowsVoting && IsLeading ? "leading" : "muted";

    /// <summary>The hero's facts and what happens next: "1h 36m · PG · Fantasy · Start the leader, or play any suggestion below".</summary>
    public string HeroLine => WatchPartyText.Join(HeroMeta, ShowsPlaying ? PlayingCaption : ShowsVoting ? LeaderCaption : ShowsStaged ? StagedCaption : "");

    public bool HasLeader => Leader is not null;

    public bool HasNoLeader => Leader is null;

    /// <summary>"FIRST SUGGESTION", or "LEADING · 2 OF 3 VOTES" once there are votes.</summary>
    public string LeaderEyebrow => Leader is { VoteCount: > 0 } leader
        ? AppText.Get("web.watchTogether.voting.leading", ("votes", leader.VoteCount), ("total", TotalVotes),
            ("total_text", AppText.Number(TotalVotes)))
        : AppText.Get("web.watchTogether.voting.firstSuggestion");

    private int TotalVotes => _party.Suggestions.Sum(s => s.VoteCount);

    /// <summary>The host's name, or "the host" as the vote's sentences name one without a name.</summary>
    private string VotingHostName => Room?.Host?.DisplayName is { Length: > 0 } name ? name : AppText.Get("web.watchTogether.voting.theHost");

    public bool IsLeading => Leader is { VoteCount: > 0 };

    public string LeaderCaption => IsHost
        ? AppText.Get("web.watchTogether.voting.hostCaption")
        : AppText.Get("web.watchTogether.voting.guestCaption", ("host", VotingHostName));

    /// <summary>"▶ Play Evan Allmächtig for everyone".</summary>
    public string PlayLeaderLabel => Leader is { } leader ? AppText.Get("web.watchTogether.voting.playForEveryone", ("title", leader.Title)) : "";

    public string VotingEmptyText => IsHost
        ? AppText.Get("web.watchTogether.voting.emptyHost")
        : AppText.Get("web.watchTogether.voting.emptyGuest", ("host", VotingHostName));

    /// <summary>The suggestions' heading: "Suggestions · ranked by votes", "Suggestions · 3" or "After this · still open for suggestions".</summary>
    public string SuggestionsHeading => IsPlaying ? AppText.Get("web.watchTogether.playing.afterTitle")
        : IsVote ? AppText.Get("web.watchTogether.voting.ranked")
        : AppText.Get("web.watchTogether.lobby.suggestionsCount", ("count", _party.Suggestions.Count));

    public string SuggestionsHint => IsPlaying ? ""
        : IsVote ? AppText.Get("web.watchTogether.voting.oneVote")
        : IsHost ? AppText.Get("watchParty.strip.manager") : AppText.Get("watchParty.strip.guest");

    public bool HasSuggestionsHint => SuggestionsHint.Length > 0;

    /// <summary>The list shows in a vote room and while a title plays; a host-pick lobby shows it once there is one.</summary>
    public bool ShowsSuggestions => Room is not null && !HasCandidate && (IsVote || IsPlaying || _party.Suggestions.Count > 0);

    public bool HasNoSuggestions => SuggestionRows.Count == 0;

    public string SuggestionsEmptyText => IsPlaying ? AppText.Get("web.watchTogether.playing.empty") : AppText.Get("web.watchTogether.voting.empty");

    /// <summary>The host plays the vote's leader for everyone.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task PlayLeaderAsync() => Leader is { } leader
        ? HostAsync(() => _party.PromoteAsync(leader.Id), AppText.Get("web.watchTogether.actions.promoteFailed"))
        : Task.CompletedTask;

    /// <summary>A vote on or off.</summary>
    [RelayCommand]
    private Task VoteAsync(WatchPartySuggestionRow? row) => row is { CanVote: true }
        ? MemberAsync(() => _party.SetVoteAsync(row.Id, !row.VotedByMe), AppText.Get("web.watchTogether.room.voteFailed"))
        : Task.CompletedTask;

    /// <summary>The host's "Play this" (in a vote room) or "Queue this" (a host-pick lobby stages it).</summary>
    [RelayCommand]
    private Task PromoteAsync(WatchPartySuggestionRow? row) => row is { CanPromote: true }
        ? IsVote
            ? HostAsync(() => _party.PromoteAsync(row.Id), AppText.Get("web.watchTogether.actions.promoteFailed"))
            : HostAsync(() => _party.StageAsync(row.Suggestion.ContentId), AppText.Get("web.watchTogether.actions.stageFailed"))
        : Task.CompletedTask;

    /// <summary>Takes a suggestion back.</summary>
    [RelayCommand]
    private Task DeleteSuggestionAsync(WatchPartySuggestionRow? row) => row is { CanDelete: true }
        ? MemberAsync(() => _party.RemoveSuggestionAsync(row.Id), AppText.Get("web.watchTogether.room.removeFailed"))
        : Task.CompletedTask;

    private async Task MemberAsync(Func<Task> action, string failure)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "A member's action in the Watch Party failed");
            Say(ex is Api.SiloApiException { Problem.Detail: { Length: > 0 } detail } ? detail : failure);
        }
    }

    // The rows as the room's mode and phase show them: votes in a vote room and while playing; the host's queue in a lobby.
    private void ShowSuggestions()
    {
        var room = Room;
        var suggestions = _party.Suggestions;
        string? profile = _session.ProfileId;
        // Only what the rows show moves them; a snapshot that changed nothing of it leaves them be.
        string key = string.Join("|", room?.SelectionMode, room?.Phase, room?.IsHost, profile,
            string.Join(",", (room?.Members ?? []).Select(m => m.Key + "=" + m.DisplayName)));
        if (ReferenceEquals(suggestions, _rowsFrom) && key == _rowsKey)
        {
            return;
        }
        _rowsFrom = suggestions;
        _rowsKey = key;
        bool votes = IsVote || IsPlaying;
        int maxVotes = suggestions.Count > 0 ? suggestions[0].VoteCount : 0;
        var names = (room?.Members ?? []).GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First().DisplayName);
        var server = _session.ActiveServer?.BaseUri;
        var existing = SuggestionRows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var wanted = suggestions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = SuggestionRows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(SuggestionRows[i].Id))
            {
                SuggestionRows.RemoveAt(i);
            }
        }
        for (int i = 0; i < suggestions.Count; i++)
        {
            var suggestion = suggestions[i];
            if (!existing.TryGetValue(suggestion.Id, out var row))
            {
                row = new WatchPartySuggestionRow(suggestion);
                SuggestionRows.Insert(i, row);
            }
            else if (SuggestionRows.IndexOf(row) is int at && at != i)
            {
                SuggestionRows.Move(at, i);
            }
            row.Suggestion = suggestion;
            row.SuggesterName = names.GetValueOrDefault(suggestion.SuggesterKey) ?? AppText.Get("web.watchTogether.common.someone");
            row.Share = maxVotes > 0 ? (double)suggestion.VoteCount / maxVotes : 0;
            row.IsLeading = votes && i == 0 && suggestion.VoteCount > 0;
            row.ShowsVotes = votes;
            row.CanVote = votes;
            row.CanPromote = IsHost && !IsPlaying;
            row.PromoteLabel = IsVote ? AppText.Get("web.watchTogether.suggestions.playThis") : AppText.Get("web.watchTogether.lobby.queueThis");
            row.CanDelete = IsHost || suggestion.SuggesterProfileId == profile;
            var poster = server is null ? null : ArtworkUrl.Resolve(server, suggestion.PosterUrl);
            if (!Equals(row.PosterUrl, poster))
            {
                row.PosterUrl = poster;
            }
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
            _heroRead?.Dispose();
            _heroRead = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
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
                _party.NoteTitle(WatchPartyText.StoredHeadline(item));
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
                HeroHeadline = AppText.Get("web.watchTogether.hero.unavailable");
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
            WatchPartyPlaybackStates.Playing => AppText.Get("web.watchTogether.playing.playing"),
            WatchPartyPlaybackStates.Waiting => AppText.Get("web.watchTogether.playing.waiting"),
            _ => AppText.Get("web.watchTogether.playing.paused"),
        };
        PlayingCaption = AppText.Get("web.watchTogether.playing.caption", ("clock", Clock(position)), ("state", state), ("count", room.MemberCount),
            ("count_text", AppText.Number(room.MemberCount)));
    }

    private void OnCardChosen(object? sender, WatchPartyShelfCard card)
    {
        bool stages = Shelf.Picks;
        _suggestion = null;
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
        _suggestion = null;
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
                if (await HostAsync(() => _party.StageAsync(choice.ContentId), AppText.Get("web.watchTogether.actions.stageFailed")))
                {
                    DismissCandidate();
                }
                return;
            }
            string id = _suggestion is { } pending && pending.ContentId == choice.ContentId ? pending.SuggestionId : Guid.NewGuid().ToString();
            _suggestion = (choice.ContentId, id);
            await _party.SuggestAsync(new WatchPartySuggestionCreate(id, choice.ContentId, choice.ContentType, choice.Title, choice.Subtitle,
                choice.PosterPath));
            _suggestion = null;
            Say(AppText.Get("web.watchTogether.room.suggested", ("title", choice.Title)));
            DismissCandidate();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not suggest a title");
            Say(ex is Api.SiloApiException { Problem.Detail: { Length: > 0 } detail } ? detail : AppText.Get("web.watchTogether.common.suggestFailed"));
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
