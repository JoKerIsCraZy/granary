using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Collections;

// The Titles card, as the web's ManualContentsPanel: the titles in their order, ten at first, each removable (with
// Undo for a few seconds) and movable. Before the collection exists they are only picked here and added on Create;
// after, each change saves at once.
public sealed partial class CollectionEditorViewModel
{
    /// <summary>Titles shown before "Show all".</summary>
    public const int ShownLimit = 10;

    /// <summary>How long "Removed …" offers Undo.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(6);

    private int _itemCount;
    private long _maxPosition = -1;
    private string? _orderVersion;
    private (CollectionTitleRow Row, int Index)? _removed;
    private CancellationTokenSource? _removedRun;
    private List<CollectionTitleRow> _failed = [];

    /// <summary>Every title, in the collection's order.</summary>
    public ObservableCollection<CollectionTitleRow> Titles { get; } = [];

    /// <summary>The titles the list shows: the first ten until Show all.</summary>
    public ObservableCollection<CollectionTitleRow> ShownTitles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllText))]
    public partial bool ShowsAllTitles { get; set; }

    /// <summary>"Not created yet", "Saving…", "Saved" beside the heading.</summary>
    [ObservableProperty]
    public partial string TitlesStatus { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTitlesEmpty), nameof(IsTitlesLoading), nameof(HasTitlesLoadError))]
    public partial LibraryLoadState TitlesState { get; private set; }

    /// <summary>"Couldn't add Heat. Try again." under the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTitlesError))]
    public partial string TitlesError { get; private set; } = "";

    /// <summary>The titles can be put in another order: dragged, or moved with Alt+Up and Alt+Down.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsReorderHint))]
    public partial bool CanReorder { get; private set; }

    /// <summary>More than one page of titles: only the first 200 show, and none can be moved.</summary>
    [ObservableProperty]
    public partial bool TitlesCapped { get; private set; }

    /// <summary>"Removed Heat" with Undo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoved))]
    public partial string RemovedText { get; private set; } = "";

    /// <summary>"Created, but couldn't add 2 titles" with Try again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailedAdds))]
    public partial string FailedText { get; private set; } = "";

    public string TitlesCaption => IsNew
        ? "Pick titles now. They're added when you press Create collection."
        : "In the order you see them. Titles save as you add, remove or drag them.";

    /// <summary>"3 titles" in the header.</summary>
    public string MetaLine => CollectionEditorText.Titles(TitlesCapped ? Math.Max(_itemCount, Titles.Count) : Titles.Count);

    public bool IsTitlesEmpty => Titles.Count == 0 && TitlesState is not (LibraryLoadState.Loading or LibraryLoadState.Error);

    public bool IsTitlesLoading => TitlesState == LibraryLoadState.Loading;

    public bool HasTitlesLoadError => TitlesState == LibraryLoadState.Error;

    public bool HasTitlesError => TitlesError.Length > 0;

    public bool HasRemoved => RemovedText.Length > 0;

    public bool HasFailedAdds => FailedText.Length > 0;

    public bool CanShowAll => Titles.Count > ShownLimit;

    public string ShowAllText => ShowsAllTitles ? "Show fewer" : $"Show all {Titles.Count}";

    public bool ShowsReorderHint => CanReorder && Titles.Count > 1;

    [RelayCommand]
    private void ToggleShowAll() => ShowsAllTitles = !ShowsAllTitles;

    partial void OnShowsAllTitlesChanged(bool value) => ShowTitles();

    /// <summary>Puts the title in: picked for a new collection, saved at the end of a created one.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AddTitleAsync(CollectionSearchResult? result)
    {
        if (result is null || Titles.Any(t => t.Id == result.Id))
        {
            return;
        }
        TitlesError = "";
        if (IsNew)
        {
            Titles.Add(new CollectionTitleRow(result.Id, result.Title, result.Meta, result.PosterUrl, Titles.Count, RemoveTitleCommand));
            TitlesChanged();
            return;
        }
        await SerializedAsync(() => AddNowAsync(result));
    }

    private async Task AddNowAsync(CollectionSearchResult result)
    {
        if (CollectionId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server } || Titles.Any(t => t.Id == result.Id))
        {
            return;
        }
        long position = Math.Max(_maxPosition + 1, TitlesCapped ? _itemCount : 0);
        TitlesStatus = "Saving…";
        try
        {
            await _collections.AddItemAsync(server.BaseUri, id, result.Id, position);
            _maxPosition = Math.Max(_maxPosition, position);
            if (Titles.All(t => t.Id != result.Id))
            {
                Titles.Add(new CollectionTitleRow(result.Id, result.Title, result.Meta, result.PosterUrl, position, RemoveTitleCommand));
            }
            TitlesStatus = "Saved";
            TitlesChanged();
            await AfterTitlesWrittenAsync(server.BaseUri, id);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not add a title to a collection");
            TitlesStatus = "";
            TitlesError = $"Couldn't add {result.Title}. {Why(ex)}";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RemoveTitleAsync(CollectionTitleRow? row)
    {
        int index = row is null ? -1 : Titles.IndexOf(row);
        if (row is null || index < 0)
        {
            return;
        }
        TitlesError = "";
        if (IsNew)
        {
            Titles.Remove(row);
            OfferUndo(row, index);
            TitlesChanged();
            return;
        }
        await SerializedAsync(() => RemoveNowAsync(row));
    }

    private async Task RemoveNowAsync(CollectionTitleRow row)
    {
        int index = Titles.IndexOf(row);
        if (index < 0 || CollectionId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        TitlesStatus = "Saving…";
        try
        {
            await _collections.RemoveItemAsync(server.BaseUri, id, row.Id);
            Titles.Remove(row);
            OfferUndo(row, index);
            TitlesStatus = "Saved";
            TitlesChanged();
            await AfterTitlesWrittenAsync(server.BaseUri, id);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not remove a title from a collection");
            TitlesStatus = "";
            TitlesError = $"Couldn't remove {row.Title}. {Why(ex)}";
        }
    }

    /// <summary>Puts the last removed title back where it was.</summary>
    [RelayCommand]
    private async Task UndoRemoveAsync()
    {
        if (_removed is not { } removed)
        {
            return;
        }
        ClearRemoved();
        int index = Math.Min(removed.Index, Titles.Count);
        if (IsNew)
        {
            Titles.Insert(index, removed.Row);
            TitlesChanged();
            return;
        }
        await SerializedAsync(() => PutBackAsync(removed.Row, index));
    }

    private async Task PutBackAsync(CollectionTitleRow row, int index)
    {
        if (CollectionId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var removed = (Row: row, Index: index);
        TitlesStatus = "Saving…";
        try
        {
            await _collections.AddItemAsync(server.BaseUri, id, removed.Row.Id, removed.Row.Position);
            Titles.Insert(Math.Min(index, Titles.Count), removed.Row);
            TitlesStatus = "Saved";
            TitlesChanged();
            await AfterTitlesWrittenAsync(server.BaseUri, id);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not put a title back in a collection");
            TitlesStatus = "";
            TitlesError = $"Couldn't put back {removed.Row.Title}. {Why(ex)}";
        }
    }

    /// <summary>Alt+Up on a row.</summary>
    [RelayCommand]
    private Task MoveTitleUpAsync(CollectionTitleRow? row) => MoveAsync(row, -1);

    /// <summary>Alt+Down on a row.</summary>
    [RelayCommand]
    private Task MoveTitleDownAsync(CollectionTitleRow? row) => MoveAsync(row, 1);

    /// <summary>The shown rows after a drag: the titles past them keep their places behind.</summary>
    public Task ApplyShownOrderAsync(IReadOnlyList<string> shownIds)
    {
        ArgumentNullException.ThrowIfNull(shownIds);
        var shown = shownIds.ToHashSet(StringComparer.Ordinal);
        return ReorderAsync([.. shownIds, .. Titles.Select(t => t.Id).Where(id => !shown.Contains(id))]);
    }

    /// <summary>Adds the titles that did not go in when the collection was made.</summary>
    [RelayCommand]
    private Task RetryFailedAddsAsync() => SerializedAsync(RetryFailedAddsNowAsync);

    private async Task RetryFailedAddsNowAsync()
    {
        if (CollectionId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var still = new List<CollectionTitleRow>();
        foreach (var row in _failed)
        {
            try
            {
                long position = _maxPosition + 1;
                await _collections.AddItemAsync(server.BaseUri, id, row.Id, position);
                _maxPosition = Math.Max(_maxPosition, position);
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex))
            {
                _logger.LogWarning(ex, "Could not add a title to a new collection");
                still.Add(row);
            }
        }
        await LoadTitlesAsync();
        await RefreshVersionAsync(server.BaseUri);
        ShowFailedAdds(still);
    }

    [RelayCommand]
    private Task ReloadTitlesAsync() => SerializedAsync(LoadTitlesAsync);

    private async Task LoadTitlesAsync()
    {
        if (CollectionId is not { } id || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        TitlesState = LibraryLoadState.Loading;
        TitlesError = "";
        try
        {
            var page = await _collections.ListItemsAsync(server.BaseUri, id);
            var cards = await CardsAsync(server, id);
            Titles.Clear();
            foreach (var item in page.Items.Where(i => i.MediaItemId.Length > 0))
            {
                Titles.Add(Row(item, cards, server.BaseUri));
            }
            TitlesCapped = page.Page is { HasMore: true };
            _maxPosition = page.Items.Count == 0 ? -1 : page.Items.Max(i => i.Position);
            await RefreshOrderAsync(server.BaseUri, id);
            TitlesState = LibraryLoadState.Ready;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not load a collection's titles");
            TitlesState = LibraryLoadState.Error;
        }
        TitlesChanged();
    }

    // Cards give the titles their posters and years; without them the rows still show the names.
    private async Task<Dictionary<string, CatalogItem>> CardsAsync(Servers.ServerEntry server, string id)
    {
        try
        {
            var images = await _images.GetAsync(server, ImageSizes.Default);
            var cards = await _catalog.BrowseAsync(server.BaseUri, new CatalogQuery
            {
                Source = CatalogSources.UserCollection,
                CollectionId = id,
                Limit = CatalogQuery.MaximumLimit,
                Images = images,
            });
            return cards.Items.Where(c => c.ContentId.Length > 0).DistinctBy(c => c.ContentId).ToDictionary(c => c.ContentId, StringComparer.Ordinal);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not load a collection's cards");
            return [];
        }
    }

    private CollectionTitleRow Row(PersonalCollectionItem item, Dictionary<string, CatalogItem> cards, Uri server) =>
        cards.TryGetValue(item.MediaItemId, out var card)
            ? new CollectionTitleRow(item.MediaItemId, card.Title, CollectionEditorText.TitleMeta(card.Year, card.Type), ArtworkUrl.Resolve(server, card.PosterUrl),
                item.Position, RemoveTitleCommand)
            : new CollectionTitleRow(item.MediaItemId, item.Title ?? item.MediaItemId, "", null, item.Position, RemoveTitleCommand);

    private async Task MoveAsync(CollectionTitleRow? row, int by)
    {
        int index = row is null ? -1 : Titles.IndexOf(row);
        if (row is null || !CanReorder || index < 0 || index + by < 0 || index + by >= Titles.Count)
        {
            return;
        }
        var ids = Titles.Select(t => t.Id).ToList();
        ids.RemoveAt(index);
        ids.Insert(index + by, row.Id);
        if (index + by >= ShownLimit)
        {
            // The row goes past the first ten: they all show, so it stays in view.
            ShowsAllTitles = true;
        }
        await ReorderAsync(ids);
    }

    // Shows the new order at once; a created collection saves it at the order's version, and reads the titles again
    // when that fails.
    private async Task ReorderAsync(IReadOnlyList<string> ids)
    {
        var rows = Titles.ToDictionary(t => t.Id, StringComparer.Ordinal);
        if (!CanReorder || ids.SequenceEqual(Titles.Select(t => t.Id)) || ids.Count != Titles.Count || !ids.All(rows.ContainsKey))
        {
            // A drag may have moved the shown rows already: they show the order as it is.
            ResyncShown();
            return;
        }
        // A removed title's place is gone with the old order: no Undo past a move.
        ClearRemoved();
        Titles.Clear();
        foreach (string id in ids)
        {
            Titles.Add(rows[id]);
        }
        TitlesChanged();
        if (!IsNew)
        {
            await SerializedAsync(() => SaveOrderAsync(ids));
        }
    }

    private async Task SaveOrderAsync(IReadOnlyList<string> ids)
    {
        if (CollectionId is not { } collection || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        TitlesStatus = "Saving…";
        try
        {
            _orderVersion ??= (await _collections.GetOrderAsync(server.BaseUri, collection)).Version;
            var saved = await _collections.SetOrderAsync(server.BaseUri, collection, _orderVersion, ids);
            _orderVersion = saved.Version;
            TitlesStatus = "Saved";
            await RefreshVersionAsync(server.BaseUri);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not save a collection's order");
            TitlesStatus = "";
            await LoadTitlesAsync();
            TitlesError = ex is SiloApiException { Status: 412 }
                ? "The order changed elsewhere. It's shown as it is now."
                : $"Couldn't save the order. {Why(ex)}";
        }
    }

    // Each title written changes the collection's version and its order's.
    private async Task AfterTitlesWrittenAsync(Uri server, string id)
    {
        await RefreshVersionAsync(server);
        await RefreshOrderAsync(server, id);
    }

    // The order can be changed while the server has it all, as these rows show it.
    private async Task RefreshOrderAsync(Uri server, string id)
    {
        _orderVersion = null;
        if (!_capabilities.ItemReorder || TitlesCapped)
        {
            CanReorder = false;
            return;
        }
        try
        {
            var order = await _collections.GetOrderAsync(server, id);
            _orderVersion = order.Version;
            CanReorder = !order.Value.HasMore && order.Value.OrderedIds.SequenceEqual(Titles.Select(t => t.Id));
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not read a collection's order");
            CanReorder = false;
        }
    }

    private void OfferUndo(CollectionTitleRow row, int index)
    {
        ClearRemoved();
        _removed = (row, index);
        RemovedText = $"Removed {row.Title}";
        var run = new CancellationTokenSource();
        _removedRun = run;
        _ = ForgetRemovedAsync(run.Token);
    }

    private async Task ForgetRemovedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(UndoWindow, _time, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // A newer removal ended this one's window already.
        if (!token.IsCancellationRequested)
        {
            ClearRemoved();
        }
    }

    private void ClearRemoved()
    {
        _removedRun?.Cancel();
        _removedRun?.Dispose();
        _removedRun = null;
        _removed = null;
        RemovedText = "";
    }

    private void ShowFailedAdds(List<CollectionTitleRow> failed)
    {
        _failed = failed;
        FailedText = failed.Count == 0 ? "" : $"Created, but couldn't add {CollectionEditorText.Titles(failed.Count)}";
    }

    // The list, the numbers, and everything that counts the titles.
    private void TitlesChanged()
    {
        for (int i = 0; i < Titles.Count; i++)
        {
            Titles[i].Number = i + 1;
        }
        if (IsNew)
        {
            // A picked title can be moved before the collection exists.
            CanReorder = Titles.Count > 1;
        }
        ShowTitles();
        OnPropertyChanged(nameof(MetaLine));
        OnPropertyChanged(nameof(IsTitlesEmpty));
        OnPropertyChanged(nameof(CanShowAll));
        OnPropertyChanged(nameof(ShowAllText));
        OnPropertyChanged(nameof(ShowsReorderHint));
        MarkResults();
        DraftChanged();
    }

    private void ResyncShown()
    {
        ShownTitles.Clear();
        ShowTitles();
    }

    private void ShowTitles()
    {
        var shown = ShowsAllTitles || Titles.Count <= ShownLimit ? Titles.ToList() : Titles.Take(ShownLimit).ToList();
        if (shown.SequenceEqual(ShownTitles))
        {
            return;
        }
        ShownTitles.Clear();
        foreach (var row in shown)
        {
            ShownTitles.Add(row);
        }
    }

    private static string Why(Exception ex) => ex is SiloApiException ? LibraryErrors.Describe(ex) : "Try again.";
}
