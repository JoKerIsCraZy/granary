using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.WatchParty;

// The sheet's episode chooser, as the web's EpisodeChooser in StartPartySheet.tsx: the series' seasons as pills, opening on
// the page's season (else the first that is not the specials), and the season's episodes that have a file to play.
// Picking one puts it up and closes the chooser; "Keep the current pick" closes it as it was.
public sealed partial class StartPartyViewModel
{
    private CancellationTokenSource? _episodeReads;

    public ObservableCollection<StartPartySeason> Seasons { get; } = [];

    public ObservableCollection<StartPartyEpisode> Episodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPick))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsChoosing { get; private set; }

    /// <summary>The Episode row, with Change: a series, season or episode while not choosing.</summary>
    public bool ShowsPick => HasEpisodeChoice && !IsChoosing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEpisodes))]
    public partial bool IsLoadingEpisodes { get; private set; }

    /// <summary>"No playable episodes here." once a season's list came back without any.</summary>
    public bool HasNoEpisodes => !IsLoadingEpisodes && _listed && Episodes.Count == 0;

    // A season's list came back (empty or not).
    private bool _listed;

    /// <summary>"Change" or "Choose": the chooser opens, its seasons read the first time.</summary>
    [RelayCommand]
    private async Task ChooseAsync()
    {
        if (Request?.SeriesId is not { } seriesId || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        IsChoosing = true;
        if (Seasons.Count > 0)
        {
            return;
        }
        int opening = _opening;
        var token = _reads.Token;
        try
        {
            var seasons = await _catalog.ListSeasonsAsync(server.BaseUri, seriesId, cancellationToken: token);
            if (opening != _opening || Seasons.Count > 0)
            {
                return;
            }
            // The specials last, as the page lists its seasons (the apps' order).
            var ordered = seasons.Items.OrderBy(s => DetailFacts.IsSpecials(s) ? 1 : 0).ThenBy(s => s.SeasonNumber).ToList();
            foreach (var season in ordered)
            {
                Seasons.Add(new StartPartySeason(season.SeasonNumber, DetailFacts.IsSpecials(season) ? "Specials" : $"Season {season.SeasonNumber}"));
            }
            int? opensOn = Request?.SeasonNumber is { } number && ordered.Any(s => s.SeasonNumber == number)
                ? number
                : (ordered.FirstOrDefault(s => !DetailFacts.IsSpecials(s)) ?? ordered.FirstOrDefault())?.SeasonNumber;
            if (Seasons.FirstOrDefault(s => s.Number == opensOn) is { } first)
            {
                await PickSeasonAsync(first);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The sheet closed or opened again.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is ArgumentException)
        {
            // Nothing to choose from: the row with Change comes back, as it was.
            _logger.LogWarning("The series' seasons could not be read for a Watch Party: {Failure}", ex.GetType().Name);
            LeaveChooser(opening);
        }
        catch (Exception ex)
        {
            // A command's failure would end the app: this one leaves the chooser too.
            _logger.LogError(ex, "The series' seasons could not be shown for a Watch Party");
            LeaveChooser(opening);
        }
    }

    private void LeaveChooser(int opening)
    {
        if (opening == _opening)
        {
            IsChoosing = false;
        }
    }

    /// <summary>A season pill: its playable episodes. A pill picked while another's are read takes their place.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PickSeasonAsync(StartPartySeason? season)
    {
        if (season is null || Request?.SeriesId is not { } seriesId || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        foreach (var other in Seasons)
        {
            other.IsSelected = ReferenceEquals(other, season);
        }
        _episodeReads?.Cancel();
        _episodeReads?.Dispose();
        _episodeReads = CancellationTokenSource.CreateLinkedTokenSource(_reads.Token);
        var token = _episodeReads.Token;
        Episodes.Clear();
        _listed = false;
        IsLoadingEpisodes = true;
        try
        {
            var episodes = await _catalog.ListEpisodesAsync(server.BaseUri, seriesId, season.Number, cancellationToken: token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            foreach (var episode in episodes.Items.Where(e => e.Files.Count > 0 && e.ContentId.Length > 0))
            {
                Episodes.Add(new StartPartyEpisode(episode.ContentId, season.Number, episode.EpisodeNumber, episode.Title, episode.UserData?.Played == true));
            }
            _listed = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another season, or the sheet closed.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is ArgumentException)
        {
            _logger.LogWarning("A season's episodes could not be read for a Watch Party: {Failure}", ex.GetType().Name);
        }
        catch (Exception ex)
        {
            // A command's failure would end the app: the season shows no episodes instead.
            _logger.LogError(ex, "A season's episodes could not be shown for a Watch Party");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingEpisodes = false;
                OnPropertyChanged(nameof(HasNoEpisodes));
            }
        }
    }

    /// <summary>An episode of the list: put up in place of the page's pick.</summary>
    [RelayCommand]
    private void PickEpisode(StartPartyEpisode? episode)
    {
        if (episode is null)
        {
            return;
        }
        Target = new StartPartyTarget(episode.ContentId, CatalogItemTypes.Episode, episode.Title,
            StartPartyTarget.EpisodeCode(episode.SeasonNumber, episode.EpisodeNumber), Request?.SeriesTitle);
        IsChoosing = false;
    }

    /// <summary>"Keep the current pick".</summary>
    [RelayCommand]
    private void KeepCurrent() => IsChoosing = false;

    private void ResetChooser()
    {
        _episodeReads?.Cancel();
        IsChoosing = false;
        IsLoadingEpisodes = false;
        _listed = false;
        Seasons.Clear();
        Episodes.Clear();
        OnPropertyChanged(nameof(HasNoEpisodes));
    }
}
