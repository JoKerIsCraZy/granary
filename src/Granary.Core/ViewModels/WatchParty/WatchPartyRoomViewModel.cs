using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>A member in the room's list: "Elena · Host · Watching".</summary>
public sealed record WatchPartyMemberRow(string Name, bool IsHost, bool IsSelf, string Status)
{
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
}

/// <summary>A title the host can put up next: a movie, an episode, or a series by the episode it would play next.</summary>
public sealed record WatchPartyPick(string ContentId, string Title, string Meta, Uri? PosterUrl);

/// <summary>
/// The room page, as the web's (<c>pages/watchtogether/WatchTogetherRoomPage.tsx</c> and its stages): the code and invite
/// link, who is here, and the stage: an empty lobby, a title up next (the host starts it, guests mark themselves ready),
/// or what plays (back into the player, or the host stops it for everyone). The host finds titles to put up next by
/// searching. When the room starts a title, the player opens by itself, once per start: the start is kept on the session,
/// so the page shown again (a new page each time, it is not cached) after the viewer left the player does not reopen it.
/// </summary>
public sealed partial class WatchPartyRoomViewModel : ObservableObject
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    private readonly WatchPartySession _party;
    private readonly WatchPartyFeature _feature;
    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<WatchPartyRoomViewModel> _logger;
    private bool _active;
    private string? _stagedShown;
    private ITimer? _clock;
    private CancellationTokenSource? _search;

    public WatchPartyRoomViewModel(WatchPartySession party, WatchPartyFeature feature, ISessionManager session, CatalogApi catalog,
        ImageParameterCache images, INavigationService navigation, IUiDispatcher dispatcher, TimeProvider time, ILogger<WatchPartyRoomViewModel> logger)
    {
        _party = party;
        _feature = feature;
        _session = session;
        _catalog = catalog;
        _images = images;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    public ObservableCollection<WatchPartyMemberRow> Members { get; } = [];

    /// <summary>The host's search results to put up next.</summary>
    public ObservableCollection<WatchPartyPick> Picks { get; } = [];

    [ObservableProperty]
    public partial WatchPartyRoom? Room { get; private set; }

    /// <summary>The title up next or playing.</summary>
    [ObservableProperty]
    public partial string StagedTitle { get; private set; } = "";

    [ObservableProperty]
    public partial string StagedMeta { get; private set; } = "";

    [ObservableProperty]
    public partial Uri? StagedPoster { get; private set; }

    /// <summary>"1:02:13 · Playing · 3 here" while a title plays.</summary>
    [ObservableProperty]
    public partial string PlayingCaption { get; private set; } = "";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial string SearchState { get; private set; } = "";

    /// <summary>A host action that failed, or a copy the viewer made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(QueueCommand))]
    public partial bool IsBusy { get; private set; }

    public bool HasMessage => Message.Length > 0;

    public bool HasRoom => Room is not null;

    public bool IsHost => Room?.IsHost == true;

    public bool IsGuest => Room is not null && !Room.IsHost;

    public string Code => Room?.Code ?? "";

    public bool IsConnecting => Room is not null && !_party.IsConnected && _party.End is null;

    public bool IsLobbyEmpty => Room is { IsLobby: true, IsStaged: false };

    public bool IsStaged => Room?.IsStaged == true;

    public bool IsPlaying => Room?.IsPlaying == true;

    public bool IsEnded => _party.End is not null;

    public bool IsReplaced => _party.End?.Replaced == true;

    public bool IsClosed => IsEnded && !IsReplaced;

    /// <summary>The room's end in the web's words.</summary>
    public string EndedText => _party.End?.Reason switch
    {
        null => "",
        "not_found" => "Room not found.",
        "ended" => "That watch party is no longer active.",
        "forbidden" => "You don't have access to this watch party.",
        "replaced" => _party.End?.Message ?? WatchPartyFrames.ReplacedText,
        _ => "The room has ended.",
    };

    public string HostName => Room?.Host?.DisplayName is { Length: > 0 } name ? name : "The host";

    /// <summary>The empty lobby's words: the host's moves, or what guests wait for.</summary>
    public string LobbyText => IsHost
        ? "Pick something below and it lands here for everyone to see."
        : $"{HostName} is deciding what to watch.";

    public string StagedEyebrow => IsHost ? "Up next" : $"Up next · {HostName} queued this";

    public string StagedCaption
    {
        get
        {
            var (ready, total) = GuestsReady();
            return IsHost
                ? total > 0 ? $"{ready} of {total} ready" : "Waiting for a guest to join"
                : $"Starts when {HostName} presses play · {ready} of {total} ready";
        }
    }

    public string StartLabel
    {
        get
        {
            var (ready, total) = GuestsReady();
            return total > 0 ? $"Start for everyone · {ready}/{total} ready" : "Start alone anyway";
        }
    }

    /// <summary>Guests mark themselves ready in the lobby (the server's <c>lobby_ready</c>).</summary>
    public bool CanMarkReady => IsGuest && IsStaged && _feature.Capabilities?.LobbyReady != false;

    public bool IsSelfReady => Room?.Self?.LobbyReady == true;

    public string ReadyLabel => IsSelfReady ? "Ready ✓" : "I'm ready";

    public bool CanStop => IsHost && _feature.Capabilities?.StopPlayback != false;

    public string StopHint => IsHost
        ? "Playback keeps going for the others. Stopping brings everyone back here to pick something else."
        : "Playback keeps going for the others. Rejoining puts you at the room's position.";

    public bool GuestsCanPause => Room?.GuestsCanPause == true;

    public string PolicyText => GuestsCanPause ? "Guests can pause" : "Host controls playback";

    /// <summary>The host picks while nothing plays.</summary>
    public bool ShowsPicker => IsHost && Room is { IsLobby: true };

    public string MembersHeading => Room is { } room ? $"People · {room.MemberCount}" : "People";

    /// <summary>Shows the room, follows it while the page shows, and opens the player when the room starts a title.</summary>
    public void Activate()
    {
        if (_active)
        {
            return;
        }
        _active = true;
        _party.PropertyChanged += OnPartyChanged;
        _feature.PropertyChanged += OnFeatureChanged;
        _clock = _time.CreateTimer(_ => _dispatcher.Post(ShowPlaying), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _ = _feature.RefreshAsync();
        Show();
    }

    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }
        _active = false;
        _party.PropertyChanged -= OnPartyChanged;
        _feature.PropertyChanged -= OnFeatureChanged;
        _clock?.Dispose();
        _clock = null;
        _search?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task StartAsync() => HostAsync(_party.StartAsync, "Could not start playback");

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task StopAsync() => HostAsync(_party.StopAsync, "Could not stop playback");

    /// <summary>The host puts <paramref name="pick"/> up next; nothing plays until Start.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task QueueAsync(WatchPartyPick? pick)
    {
        if (pick is null)
        {
            return;
        }
        await HostAsync(() => _party.StageAsync(pick.ContentId), "That couldn't be queued");
        if (!HasMessage)
        {
            Search = "";
        }
    }

    [RelayCommand]
    private Task ToggleGuestsCanPauseAsync() => HostAsync(() => _party.SetGuestsCanPauseAsync(!GuestsCanPause), "Could not change who controls playback");

    [RelayCommand]
    private async Task ToggleReadyAsync()
    {
        if (!await _party.SendAsync(WatchPartyFrames.LobbyReady(!IsSelfReady)))
        {
            Message = WatchPartyPlaybackBinding.ReconnectingText;
        }
    }

    /// <summary>Back into the room's playback, at its position.</summary>
    [RelayCommand]
    private void RejoinPlayback()
    {
        if (Room is { IsPlaying: true, SelectedContentId: { } content } room)
        {
            _party.StartedRevision = room.SelectionRevision;
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.WatchParty(content, room.SelectedFileId, room.SelectionRevision));
        }
    }

    /// <summary>Leaves the room on this device (a host who leaves ends it two minutes later unless they come back).</summary>
    [RelayCommand]
    private async Task LeaveAsync()
    {
        await _party.LeaveAsync();
        _navigation.NavigateTo(AppRoute.WatchParty);
    }

    /// <summary>The host ends the room for everyone (the page asked first).</summary>
    public async Task EndRoomAsync()
    {
        try
        {
            await _party.EndRoomAsync();
            _navigation.NavigateTo(AppRoute.WatchParty);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not end the Watch Party");
            Message = "Could not end the room. " + LibraryErrors.Describe(ex);
        }
    }

    /// <summary>Claims the membership back after another device took it over.</summary>
    [RelayCommand]
    private void Rejoin() => _party.Rejoin();

    /// <summary>The page copied the code or link: it says so.</summary>
    public void Copied(string what) => Message = what;

    /// <summary>The invitation link, for the page's Copy.</summary>
    public Uri? InviteLink => Room is { } room && _session.ActiveServer is { } server ? WatchPartyApi.InviteLink(server.BaseUri, room) : null;

    partial void OnSearchChanged(string value)
    {
        _search?.Cancel();
        _search = new CancellationTokenSource();
        _ = SearchLaterAsync(value.Trim(), _search.Token);
    }

    private async Task SearchLaterAsync(string text, CancellationToken token)
    {
        if (text.Length == 0)
        {
            Picks.Clear();
            SearchState = "";
            return;
        }
        try
        {
            await Task.Delay(SearchDelay, _time, token);
            if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
            {
                return;
            }
            SearchState = "Searching…";
            var images = await _images.GetAsync(server, ImageSizes.Default, token);
            var found = await _catalog.BrowseAsync(server.BaseUri, new CatalogQuery { Q = text, Limit = 12, Images = images }, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            Picks.Clear();
            foreach (var item in found.Items)
            {
                if (Pick(item, server.BaseUri) is { } pick)
                {
                    Picks.Add(pick);
                }
            }
            SearchState = Picks.Count == 0 ? "No titles match." : "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer search took over.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not search titles for the Watch Party");
            SearchState = "The search didn't work.";
        }
    }

    // A movie or an episode plays as it is; a series by the episode its card would play.
    private static WatchPartyPick? Pick(CatalogItem item, Uri server)
    {
        string? content = item.Type switch
        {
            CatalogItemTypes.Movie or CatalogItemTypes.Episode => item.ContentId,
            CatalogItemTypes.Series => item.PlayContentId,
            _ => null,
        };
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }
        string meta = string.Join(" · ", new[]
        {
            item.Year?.ToString(CultureInfo.InvariantCulture),
            item.Type switch { CatalogItemTypes.Movie => "Movie", CatalogItemTypes.Series => "Show, next episode", _ => "Episode" },
        }.Where(part => !string.IsNullOrEmpty(part)));
        string title = item.Type == CatalogItemTypes.Episode && item.SeriesTitle is { Length: > 0 } series ? $"{series}: {item.Title}" : item.Title;
        return new WatchPartyPick(content, title, meta, ArtworkUrl.Resolve(server, item.PosterUrl));
    }

    private async Task HostAsync(Func<Task> action, string failure)
    {
        IsBusy = true;
        Message = "";
        try
        {
            await action();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "{Failure}", failure);
            Message = $"{failure}. {LibraryErrors.Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAct() => !IsBusy && IsHost;

    private void OnPartyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Show();

    private void OnFeatureChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Show();

    private void Show()
    {
        Room = _party.Room;
        Members.Clear();
        foreach (var member in Room?.Members ?? [])
        {
            Members.Add(new WatchPartyMemberRow(member.DisplayName, member.IsHost, member.IsSelf, Status(member)));
        }
        foreach (string name in new[]
        {
            nameof(HasRoom), nameof(IsHost), nameof(IsGuest), nameof(Code), nameof(IsConnecting), nameof(IsLobbyEmpty), nameof(IsStaged),
            nameof(IsPlaying), nameof(IsEnded), nameof(IsReplaced), nameof(IsClosed), nameof(EndedText), nameof(HostName), nameof(LobbyText),
            nameof(StagedEyebrow), nameof(StagedCaption), nameof(StartLabel), nameof(CanMarkReady), nameof(IsSelfReady), nameof(ReadyLabel),
            nameof(CanStop), nameof(StopHint), nameof(GuestsCanPause), nameof(PolicyText), nameof(ShowsPicker), nameof(MembersHeading),
            nameof(InviteLink),
        })
        {
            OnPropertyChanged(name);
        }
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        QueueCommand.NotifyCanExecuteChanged();
        ShowStaged();
        ShowPlaying();
        AutoStart();
    }

    // The room started a title (a new revision): the player opens once for it, as the web's room page does.
    private void AutoStart()
    {
        if (!_active || IsEnded || Room is not { IsPlaying: true, SelectedContentId: { } content } room
            || _party.StartedRevision == room.SelectionRevision)
        {
            return;
        }
        _party.StartedRevision = room.SelectionRevision;
        _navigation.NavigateTo(AppRoute.Player, PlayRequest.WatchParty(content, room.SelectedFileId, room.SelectionRevision));
    }

    private void ShowStaged()
    {
        string? content = Room?.SelectedContentId;
        if (content == _stagedShown)
        {
            return;
        }
        _stagedShown = content;
        StagedTitle = "";
        StagedMeta = "";
        StagedPoster = null;
        if (content is not null)
        {
            _ = ReadStagedAsync(content);
        }
    }

    private async Task ReadStagedAsync(string content)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            var images = await _images.GetAsync(server, ImageSizes.Default);
            var item = await _catalog.GetItemAsync(server.BaseUri, content, images: images);
            if (_stagedShown != content)
            {
                return;
            }
            StagedTitle = item.Type == CatalogItemTypes.Episode && item.SeriesTitle is { Length: > 0 } series ? series : item.Title;
            StagedMeta = item.Type == CatalogItemTypes.Episode && item.SeasonNumber is { } season && item.EpisodeNumber is { } episode
                ? $"S{season} · E{episode} — {item.Title}"
                : item.Year?.ToString(CultureInfo.InvariantCulture) ?? "";
            StagedPoster = ArtworkUrl.Resolve(server.BaseUri, item.PosterUrl);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not read the title up next");
            StagedTitle = "A title";
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

    private (int Ready, int Total) GuestsReady()
    {
        var guests = Room?.Members.Where(m => !m.IsHost && m.Connected).ToList() ?? [];
        return (guests.Count(m => m.LobbyReady), guests.Count);
    }

    private string Status(WatchPartyMember member) =>
        !member.Connected ? "Away"
        : Room?.IsPlaying == true ? member.IsBuffering ? "Buffering" : member.IsSyncing ? "Syncing" : "Watching"
        : Room?.IsStaged == true && !member.IsHost ? member.LobbyReady ? "Ready" : "Not ready"
        : "Connected";

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
