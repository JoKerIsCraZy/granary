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
// Apple's client do; the page then reads the item again, so that Auto shows what the server picks now. A save that fails
// says so in the web's words and still applies to this visit's Play.
public sealed partial class ItemDetailViewModel
{
    // The item the pickers show, read again after a pick is saved; null for a page without files.
    private CatalogItemDetail? _prePlayItem;
    private PrePlayPicks _picks = PrePlayPicks.None;

    private const string AudioKind = "audio";
    private const string SubtitleKind = "subtitle";

    // Saves started and reads of the item started: only the newest read, begun after the last save, is taken.
    private int _trackSaves;
    private int _prePlayReads;

    // This page's saves still on their way (other pages share the app's queue, and read their own items).
    private int _savesOnTheWay;

    // Picks made per kind: a save's failure speaks only while its pick is still the latest of its kind.
    private int _audioPicks;
    private int _subtitlePicks;

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
        _picks = _picks with { AudioTrack = option.Ordinal, AudioAutoPending = option.Ordinal is null };
        ShowPickers();
        // The web's words (useSetAudioPreference, useDeleteAudioPreference).
        return preference is null
            ? SaveTrackChoiceAsync(AudioKind, "Failed to reset audio preference", (api, server, key, token) => api.DeleteAudioAsync(server, key, token))
            : SaveTrackChoiceAsync(AudioKind, "Failed to save audio preference", (api, server, key, token) => api.SaveAudioAsync(server, key, preference, token));
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
            DetailSubtitleKind.Off => _picks with { SubtitleMode = SubtitlePickMode.Off, SubtitleTrack = null, SubtitleAutoPending = false },
            DetailSubtitleKind.Track => _picks with
            {
                SubtitleMode = SubtitlePickMode.Track,
                SubtitleTrack = option.SelectionIndex,
                SubtitleAutoPending = false,
            },
            // The remembered track no longer applies, although the item names it until the server has forgotten it.
            _ => _picks with { SubtitleMode = SubtitlePickMode.Auto, SubtitleTrack = null, SubtitleOverrideCleared = true, SubtitleAutoPending = true },
        };
        ShowPickers();
        // The web's words (useSetSubtitlePreference, useDeleteSubtitlePreference).
        return choice is null
            ? SaveTrackChoiceAsync(SubtitleKind, "Failed to reset subtitle preference", (api, server, key, token) => api.DeleteSubtitleAsync(server, key, token))
            : SaveTrackChoiceAsync(SubtitleKind, "Failed to save subtitle preference", (api, server, key, token) => api.SaveSubtitleAsync(server, key, choice, token));
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
        _savesOnTheWay = 0;
        Pickers = PrePlayPickers.None;
    }

    private void ShowPickers() => Pickers = _prePlayItem is { } item ? PrePlayPickers.For(item, _picks) : PrePlayPickers.None;

    // The picks belong to this title's files: they go with Play of the title itself, never with another's.
    private PrePlayChoice? ChoiceFor(string target) =>
        _prePlayItem is not null && (target == _contentId || target == _item?.ContentId) ? Pickers.Choice : null;

    // In pick order through the app's one queue, which a start waits for; a failure costs only the remembered choice and
    // says so, unless a newer pick of its kind has been made since. Once this page's own saves are all done, a failed one
    // too, it reads its item again, whatever other pages still have queued. A page the viewer has left is not touched.
    private async Task SaveTrackChoiceAsync(string kind, string failure, Func<TrackPreferencesApi, Uri, string, CancellationToken, Task> write)
    {
        if (_trackPreferences is not { } api || _trackChoices is not { } writes || _serverBase is not { } server || _prePlayItem is not { } item)
        {
            return;
        }
        string key = TrackPreferencesApi.KeyFor(item.Type, item.SeriesId, _contentId);
        int generation = _generation;
        int pick = kind == AudioKind ? ++_audioPicks : ++_subtitlePicks;
        _trackSaves++;
        _savesOnTheWay++;
        ActionError = "";
        bool saved = await writes.RunAsync(kind, cancellationToken => write(api, server, key, cancellationToken));
        if (generation != _generation)
        {
            return;
        }
        _savesOnTheWay--;
        if (!saved && pick == (kind == AudioKind ? _audioPicks : _subtitlePicks))
        {
            ActionError = failure;
            // The server keeps what it had: Auto shows what the item read last says it picks.
            _picks = kind == AudioKind ? _picks with { AudioAutoPending = false } : _picks with { SubtitleAutoPending = false };
            ShowPickers();
        }
        if (_savesOnTheWay == 0)
        {
            await ReadPrePlayAgainAsync(server, generation);
        }
    }

    // The server folds a saved pick into the item's effective fields (web: invalidateItemDetails after each write). An
    // answer is taken only when it is the newest read and no save began after it: an older one would bring back the item
    // as it was. A remembered subtitle stays forgotten until the item no longer names it.
    private async Task ReadPrePlayAgainAsync(Uri server, int generation)
    {
        int read = ++_prePlayReads;
        int saves = _trackSaves;
        bool IsNewest() => generation == _generation && read == _prePlayReads && saves == _trackSaves;
        try
        {
            var item = await _catalog.GetItemAsync(server, _contentId);
            if (!IsNewest())
            {
                return;
            }
            if (item.Versions.Count == 0)
            {
                SettleAuto();
                return;
            }
            _prePlayItem = item;
            _picks = _picks with
            {
                AudioAutoPending = false,
                SubtitleAutoPending = false,
                SubtitleOverrideCleared = _picks.SubtitleOverrideCleared && item.EffectiveSubtitleTrackSignature is not null,
            };
            ShowPickers();
        }
        catch (Exception ex) when (ex is ArgumentException || LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("Reading item {ItemId} again after a track choice failed: {Failure}", _contentId, LibraryErrors.Describe(ex));
            if (IsNewest())
            {
                SettleAuto();
            }
        }
        catch (Exception ex)
        {
            // Nothing a picker shows is worth more than the page: logged, never thrown into the command.
            _logger.LogError(ex, "Reading item {ItemId} again after a track choice failed unexpectedly", _contentId);
            if (IsNewest())
            {
                SettleAuto();
            }
        }
    }

    // No answer to wait for any more: Auto shows what the item read last says it picks, never a bare "Auto" for good. A
    // forgotten subtitle stays forgotten, and Play still sends its automatic track.
    private void SettleAuto()
    {
        _picks = _picks with { AudioAutoPending = false, SubtitleAutoPending = false };
        ShowPickers();
    }
}
