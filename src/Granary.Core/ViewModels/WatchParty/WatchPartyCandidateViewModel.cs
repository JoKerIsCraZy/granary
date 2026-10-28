using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Settings;
using Granary.Core.WatchParty;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>What the stage hands back once the viewer confirms: a movie, or one episode of a series.</summary>
/// <param name="ContentType"><c>movie</c> or <c>episode</c>.</param>
/// <param name="PosterPath">The poster as the server named it.</param>
public sealed record WatchPartyChoice(string ContentId, string ContentType, string Title, string Subtitle, string? PosterPath);

/// <summary>An episode of the candidate series: "3 · The Hollow · 44m".</summary>
public sealed record WatchPartyEpisodeRow(string ContentId, int SeasonNumber, int EpisodeNumber, string Title, string Runtime)
{
    public string Label => WatchPartyText.Join(EpisodeNumber.ToString(CultureInfo.InvariantCulture), Title, Runtime);
}

/// <summary>
/// A card from the shelf previewed on the stage, as the web's (<c>room/browse/CandidateStage.tsx</c>): a movie with its
/// facts and who in the room has seen it, or a series with its seasons and episodes to pick one from. Nothing reaches
/// the server until the viewer confirms; confirming stages it (the host of a host-pick lobby) or suggests it.
/// </summary>
public sealed partial class WatchPartyCandidateViewModel : ObservableObject
{
    private readonly WatchPartySession _party;
    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly ILogger _logger;
    private CancellationTokenSource? _reads;

    public WatchPartyCandidateViewModel(WatchPartySession party, ISessionManager session, CatalogApi catalog, ImageParameterCache images, ILogger logger)
    {
        _party = party;
        _session = session;
        _catalog = catalog;
        _images = images;
        _logger = logger;
    }

    /// <summary>The viewer confirmed the movie or the picked episode.</summary>
    public event EventHandler<WatchPartyChoice>? Confirmed;

    /// <summary>The viewer put the candidate away.</summary>
    public event EventHandler? Dismissed;

    public ObservableCollection<WatchPartySeenRow> Seen { get; } = [];

    public ObservableCollection<SettingChoice> Seasons { get; } = [];

    public ObservableCollection<WatchPartyEpisodeRow> Episodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeries), nameof(IsMovie), nameof(Title), nameof(Poster))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial WatchPartyShelfCard? Card { get; private set; }

    [ObservableProperty]
    public partial string Headline { get; private set; } = "";

    [ObservableProperty]
    public partial string Meta { get; private set; } = "";

    [ObservableProperty]
    public partial string Overview { get; private set; } = "";

    /// <summary>"new for everyone", "a rewatch for everyone", "1 of 2 started", "1 of 2".</summary>
    [ObservableProperty]
    public partial string SeenSummary { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel), nameof(EpisodeActionLabel))]
    public partial bool Stages { get; private set; }

    /// <summary>"Replaces Heat (1995)" when staging would take the place of what is up next.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplaces))]
    public partial string Replaces { get; private set; } = "";

    [ObservableProperty]
    public partial SettingChoice? SelectedSeason { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(PickedText))]
    public partial WatchPartyEpisodeRow? SelectedEpisode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsSeries => Card?.IsSeries == true;

    public bool IsMovie => Card is { IsSeries: false };

    public string Title => Card?.Title ?? "";

    public Uri? Poster => Card?.PosterUrl;

    public bool HasReplaces => Replaces.Length > 0;

    public string ConfirmLabel => Stages ? "Stage it for the room" : "Suggest this";

    public string EpisodeActionLabel => Stages ? "Stage it" : "Suggest";

    /// <summary>"S2 E3 · The Hollow" once an episode is picked.</summary>
    public string PickedText => SelectedEpisode is { } e
        ? WatchPartyText.Join($"S{e.SeasonNumber} E{e.EpisodeNumber}", e.Title)
        : "Pick an episode.";

    /// <summary>Shows <paramref name="card"/> on the stage.</summary>
    /// <param name="stages">Confirming stages it rather than suggesting it.</param>
    /// <param name="replaces">What is up next and would be replaced, when staging.</param>
    public Task ShowAsync(WatchPartyShelfCard card, bool stages, string? replaces)
    {
        ArgumentNullException.ThrowIfNull(card);
        _reads?.Cancel();
        _reads = new CancellationTokenSource();
        Card = card;
        Stages = stages;
        Replaces = stages && replaces is { Length: > 0 } title ? $"Replaces {title}" : "";
        Headline = card.Year is { } year ? $"{card.Title} ({year.ToString(CultureInfo.InvariantCulture)})" : card.Title;
        Meta = "";
        Overview = "";
        SeenSummary = "";
        Seen.Clear();
        Seasons.Clear();
        Episodes.Clear();
        SelectedEpisode = null;
        SelectedSeason = null;
        return card.IsSeries ? ReadSeriesAsync(card, _reads.Token) : ReadMovieAsync(card, _reads.Token);
    }

    public void Clear()
    {
        _reads?.Cancel();
        Card = null;
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (Card is not { } card)
        {
            return;
        }
        if (!card.IsSeries)
        {
            Confirmed?.Invoke(this, new WatchPartyChoice(card.ContentId, CatalogItemTypes.Movie, card.Title,
                card.Year?.ToString(CultureInfo.InvariantCulture) ?? "", card.PosterPath));
        }
        else if (SelectedEpisode is { } episode)
        {
            Confirmed?.Invoke(this, new WatchPartyChoice(episode.ContentId, CatalogItemTypes.Episode, episode.Title.Length > 0 ? episode.Title : card.Title,
                $"{card.Title} · S{episode.SeasonNumber} E{episode.EpisodeNumber}", card.PosterPath));
        }
    }

    private bool CanConfirm() => !IsBusy && Card is { } card && (!card.IsSeries || SelectedEpisode is not null);

    [RelayCommand]
    private void Dismiss() => Dismissed?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedSeasonChanged(SettingChoice? value)
    {
        if (value is not null && Card is { IsSeries: true } card && _reads is { } reads)
        {
            _ = ReadEpisodesAsync(card, int.Parse(value.Value, CultureInfo.InvariantCulture), reads.Token);
        }
    }

    private async Task ReadMovieAsync(WatchPartyShelfCard card, CancellationToken token)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            var images = await _images.GetAsync(server, ImageSizes.Default, token);
            var detail = _catalog.GetItemAsync(server.BaseUri, card.ContentId, images: images, cancellationToken: token);
            var seen = _party.GetMemberStatesAsync([card.ContentId], token);
            var item = await detail;
            if (token.IsCancellationRequested)
            {
                return;
            }
            Headline = WatchPartyText.Headline(item);
            Meta = WatchPartyText.Meta(item);
            Overview = item.Overview ?? "";
            ShowSeen(await seen, card.ContentId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another candidate took its place.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("The candidate could not be read: {Failure}", ex.GetType().Name);
        }
    }

    private void ShowSeen(Api.WatchParty.WatchPartyMemberStates? states, string contentId)
    {
        if (_party.Room is not { } room)
        {
            return;
        }
        var tints = WatchPartyMembers.Tints(room.Members);
        var byMember = (states?.Items.FirstOrDefault(i => i.ContentId == contentId)?.Members ?? [])
            .GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        Seen.Clear();
        int watched = 0;
        int started = 0;
        foreach (var member in room.Members)
        {
            byMember.TryGetValue(member.Key, out var state);
            watched += state?.State == "watched" ? 1 : 0;
            started += state?.State is "watched" or "in_progress" ? 1 : 0;
            Seen.Add(new WatchPartySeenRow(member.DisplayName, tints.GetValueOrDefault(member.Key), SeenState(state), state?.OnWatchlist == true));
        }
        int count = room.Members.Count;
        SeenSummary = count == 0 ? ""
            : started == 0 ? "new for everyone"
            : watched == count ? "a rewatch for everyone"
            : watched == 0 ? $"{started} of {count} started"
            : $"{watched} of {count}";
    }

    private static string SeenState(Api.WatchParty.WatchPartyMemberWatchState? state)
    {
        if (state is null || state.State == "unseen")
        {
            return "Hasn't seen it";
        }
        if (state.State == "watched")
        {
            return "Watched";
        }
        return state.PositionSeconds is { } position && state.DurationSeconds is double duration && duration > 0
            ? $"{Math.Round(position / duration * 100).ToString(CultureInfo.InvariantCulture)}% in"
            : "In progress";
    }

    // The seasons, then the one the room is on (or the first that is not specials).
    private async Task ReadSeriesAsync(WatchPartyShelfCard card, CancellationToken token)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            var seasons = await _catalog.ListSeasonsAsync(server.BaseUri, card.ContentId, cancellationToken: token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            foreach (var season in seasons.Items.Where(s => s.EpisodeCount > 0 || s.IsSpecials != true))
            {
                Seasons.Add(new SettingChoice(season.SeasonNumber.ToString(CultureInfo.InvariantCulture),
                    season.Title.Length > 0 ? season.Title : $"Season {season.SeasonNumber}"));
            }
            SelectedSeason = Seasons.FirstOrDefault(s => card.Season is { } on && s.Value == on.ToString(CultureInfo.InvariantCulture))
                ?? Seasons.FirstOrDefault(s => s.Value != "0") ?? Seasons.FirstOrDefault();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another candidate took its place.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("The candidate's seasons could not be read: {Failure}", ex.GetType().Name);
        }
    }

    // The season's episodes that have a file to play.
    private async Task ReadEpisodesAsync(WatchPartyShelfCard card, int season, CancellationToken token)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            var episodes = await _catalog.ListEpisodesAsync(server.BaseUri, card.ContentId, season, cancellationToken: token);
            if (token.IsCancellationRequested || SelectedSeason?.Value != season.ToString(CultureInfo.InvariantCulture))
            {
                return;
            }
            Episodes.Clear();
            SelectedEpisode = null;
            foreach (var episode in episodes.Items.Where(e => e.Files.Count > 0))
            {
                Episodes.Add(new WatchPartyEpisodeRow(episode.ContentId, episode.SeasonNumber, episode.EpisodeNumber, episode.Title,
                    WatchPartyText.Runtime(episode.Runtime)));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another candidate or season took its place.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("The candidate's episodes could not be read: {Failure}", ex.GetType().Name);
        }
    }
}
