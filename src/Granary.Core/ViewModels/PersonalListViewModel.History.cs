using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// History's watch data, as the web's catalog page offers it there (pages/Catalog.tsx, lib/historyRemoval.ts): Select,
// pick titles in the grid, Remove Selected after asking, which clears their history, watched state and resume progress.
public sealed partial class PersonalListViewModel
{
    /// <summary>The titles picked in the grid while History selects.</summary>
    public CardSelection Selection { get; } = new();

    /// <summary>History itself (not a collection): the Watch History panel with Select shows.</summary>
    public bool IsHistory => Source == CatalogSources.History && _collection is null;

    /// <summary>What the last removal said: "Removed watch data for 3 items", or why it failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemovalNotice))]
    public partial string RemovalNotice { get; private set; } = "";

    [ObservableProperty]
    public partial bool RemovalFailed { get; private set; }

    /// <summary>A removal is on its way: Remove waits.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    public partial bool IsRemoving { get; private set; }

    public bool HasRemovalNotice => RemovalNotice.Length > 0;

    /// <summary>The question before removing: "Remove selected watch data?", or one title's.</summary>
    public string RemovalTitle => Targets() is { Count: > 1 } ? AppText.Get("web.catalog.history.dialogTitleSelected")
        : Targets() is [{ Scope: HistoryRemovalScopes.Show }] ? AppText.Get("web.catalog.history.dialogTitleShow")
        : AppText.Get("web.catalog.history.dialogTitle");

    public string RemovalDescription => Targets() switch
    {
        { Count: > 1 } targets => AppText.Get("web.catalog.history.dialogBodySelected", ("count", targets.Count),
            ("selected", AppText.Number(targets.Count))),
        [{ Scope: HistoryRemovalScopes.Show }] => AppText.Get("web.catalog.history.dialogBodyShow"),
        _ => AppText.Get("web.catalog.history.dialogBody"),
    };

    [RelayCommand]
    private void StartSelecting()
    {
        RemovalNotice = "";
        Selection.Start();
    }

    /// <summary>The web's Select Loaded: every title the grid has loaded so far.</summary>
    [RelayCommand]
    private void SelectLoaded() => Selection.PickAll(Grid.Cards);

    [RelayCommand]
    private void ClearSelection() => Selection.Clear();

    [RelayCommand]
    private void StopSelecting() => Selection.Stop();

    /// <summary>Clears the picked titles' watch data; the selecting ends and the list loads again.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveSelectedAsync()
    {
        var targets = Targets();
        if (targets.Count == 0 || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        IsRemoving = true;
        try
        {
            await _catalog.RemoveHistoryAsync(server.BaseUri, targets);
            RemovalFailed = false;
            RemovalNotice = AppText.Get("web.catalog.history.removed", ("count", targets.Count));
            Selection.Stop();
            // Continue Watching, Next Up and watched marks change with it.
            _changes?.Report();
            await StartAsync();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not remove watch history");
            RemovalFailed = true;
            // The web's "Failed to remove history" with why, as one sentence for each reason.
            RemovalNotice = AppText.Get("granary.lists.removeFailed", ("reason", ex switch
            {
                SiloApiException { Status: 401 or 403 } => "denied",
                SiloApiException { Status: 404 } => "gone",
                SiloApiException { Status: 429 or 503 } or HttpRequestException or OperationCanceledException => "busy",
                _ => "other",
            }));
        }
        finally
        {
            IsRemoving = false;
        }
    }

    private bool CanRemove() => !IsRemoving;

    [RelayCommand]
    private void DismissRemovalNotice() => RemovalNotice = "";

    // Another list or collection: the History panel and its picks are left behind.
    private void LeaveHistory()
    {
        Selection.Stop();
        RemovalNotice = "";
        OnPropertyChanged(nameof(IsHistory));
    }

    private List<HistoryRemovalTarget> Targets() => [.. Selection.Cards.Select(card => HistoryRemovalTarget.For(card.Id, card.Type))];
}
