using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// The Version, Audio and Subs pickers under the buttons of a movie or episode (Silo's web detail page, ActionBar.tsx),
// which Play, Resume and Start over pass on. A version pick lasts for the visit and keeps the server from swapping in
// another file, as the web, Android and Apple clients pin a version the viewer chose. Audio and subtitle picks are also
// saved for the title (for its series, on an episode), and Auto forgets the saved one, as the web's detail page and
// Apple's client do; the page then reads the item again, so that Auto shows what the server picks now.
public sealed partial class ItemDetailViewModel
{
    // The item the pickers show, read again after a pick is saved; null for a page without files.
    private CatalogItemDetail? _prePlayItem;
    private PrePlayPicks _picks = PrePlayPicks.None;

    /// <summary>The pickers' rows and words, and what Play passes on.</summary>
    [ObservableProperty]
    public partial PrePlayPickers Pickers { get; private set; } = PrePlayPickers.None;

    /// <summary>Picks <paramref name="option"/>'s file for Play. Another file starts its audio and subtitles from Auto again: their ordinals are the file's own.</summary>
    [RelayCommand]
    private void SelectVersion(DetailVersionOption? option)
    {
        if (option is null || _prePlayItem is null)
        {
            return;
        }
        _picks = option.FileId == Pickers.Version?.FileId
            ? _picks with { FileId = option.FileId }
            : _picks with { FileId = option.FileId, AudioTrack = null, SubtitleMode = SubtitlePickMode.Auto, SubtitleTrack = null };
        ShowPickers();
    }

    /// <summary>Starts with <paramref name="option"/>'s track, saved for the title; Auto forgets the saved one.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SelectAudioAsync(DetailAudioOption? option)
    {
        if (option is null || Pickers.Version is null)
        {
            return Task.CompletedTask;
        }
        var preference = option.Ordinal is { } ordinal ? Pickers.AudioPreferenceFor(ordinal) : null;
        if (option.Ordinal is not null && preference is null)
        {
            return Task.CompletedTask;
        }
        _picks = _picks with { AudioTrack = option.Ordinal };
        ShowPickers();
        return SaveTrackChoiceAsync("audio", (api, server, key, cancellationToken) => preference is null
            ? api.DeleteAudioAsync(server, key, cancellationToken)
            : api.SaveAudioAsync(server, key, preference, cancellationToken));
    }

    /// <summary>Starts with <paramref name="option"/>'s track, or none, saved for the title; Auto forgets the saved one.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SelectSubtitleAsync(DetailSubtitleOption? option)
    {
        if (option is null || Pickers.Version is null)
        {
            return Task.CompletedTask;
        }
        var choice = option.Kind switch
        {
            DetailSubtitleKind.Off => SubtitleChoice.Off,
            DetailSubtitleKind.Track => Pickers.SubtitleChoiceFor(option.SelectionIndex),
            _ => null,
        };
        if (option.Kind == DetailSubtitleKind.Track && choice is null)
        {
            return Task.CompletedTask;
        }
        _picks = option.Kind switch
        {
            DetailSubtitleKind.Off => _picks with { SubtitleMode = SubtitlePickMode.Off, SubtitleTrack = null },
            DetailSubtitleKind.Track => _picks with { SubtitleMode = SubtitlePickMode.Track, SubtitleTrack = option.SelectionIndex },
            // The remembered track no longer applies, although the item says so until it is read again.
            _ => _picks with { SubtitleMode = SubtitlePickMode.Auto, SubtitleTrack = null, SubtitleOverrideCleared = true },
        };
        ShowPickers();
        return SaveTrackChoiceAsync("subtitle", (api, server, key, cancellationToken) => choice is null
            ? api.DeleteSubtitleAsync(server, key, cancellationToken)
            : api.SaveSubtitleAsync(server, key, choice, cancellationToken));
    }

    private void ShowPrePlay(CatalogItemDetail item)
    {
        _prePlayItem = item.Versions.Count > 0 ? item : null;
        _picks = PrePlayPicks.None;
        ShowPickers();
    }

    private void ResetPrePlay()
    {
        _prePlayItem = null;
        _picks = PrePlayPicks.None;
        Pickers = PrePlayPickers.None;
    }

    private void ShowPickers() => Pickers = _prePlayItem is { } item ? PrePlayPickers.For(item, _picks) : PrePlayPickers.None;

    // The picks belong to this title's files: they go with Play of the title itself, never with another's.
    private PrePlayChoice? ChoiceFor(string target) =>
        _prePlayItem is not null && (target == _contentId || target == _item?.ContentId) ? Pickers.Choice : null;

    // In pick order through the app's one queue, which a start waits for; a failure is logged there and costs only the
    // remembered choice. The last write of a burst reads the item again, a failed one too: an Auto the server did not
    // take then shows the remembered track again, the one a start would play.
    private async Task SaveTrackChoiceAsync(string kind, Func<TrackPreferencesApi, Uri, string, CancellationToken, Task> write)
    {
        if (_trackPreferences is not { } api || _trackChoices is not { } writes || _serverBase is not { } server || _prePlayItem is not { } item)
        {
            return;
        }
        string key = TrackPreferencesApi.KeyFor(item.Type, item.SeriesId, _contentId);
        int generation = _generation;
        await writes.RunAsync(kind, cancellationToken => write(api, server, key, cancellationToken));
        if (writes.IsIdle && generation == _generation)
        {
            await ReadPrePlayAgainAsync(server, generation);
        }
    }

    // The server folds a saved pick into the item's effective fields (web: invalidateItemDetails after each write).
    private async Task ReadPrePlayAgainAsync(Uri server, int generation)
    {
        try
        {
            var item = await _catalog.GetItemAsync(server, _contentId);
            if (generation == _generation && item.Versions.Count > 0)
            {
                _prePlayItem = item;
                _picks = _picks with { SubtitleOverrideCleared = false };
                ShowPickers();
            }
        }
        catch (Exception ex) when (ex is ArgumentException || ApiFailureText.IsRequestFailure(ex))
        {
            // The pickers keep what they show; the start reads the preferences itself.
            _logger.LogWarning("Reading item {ItemId} again after a track choice failed: {Failure}", _contentId, ApiFailureText.Describe(ex));
        }
        catch (Exception ex)
        {
            // Nothing a picker shows is worth more than the page: logged, never thrown into the command.
            _logger.LogError(ex, "Reading item {ItemId} again after a track choice failed unexpectedly", _contentId);
        }
    }
}
