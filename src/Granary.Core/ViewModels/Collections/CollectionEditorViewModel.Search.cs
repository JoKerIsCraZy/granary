using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Collections;

// "Add a title": a search of every library the profile sees, as the web's combobox, after a short pause in the typing.
public sealed partial class CollectionEditorViewModel
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Results a search shows.</summary>
    public const int ResultLimit = 12;

    private CancellationTokenSource? _searchRun;

    public ObservableCollection<CollectionSearchResult> Results { get; } = [];

    /// <summary>The search box.</summary>
    [ObservableProperty]
    public partial string TitleQuery { get; set; } = "";

    /// <summary>"Searching…", "No titles match.", or the failed search's words; empty with results or no search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchState))]
    public partial string SearchState { get; private set; } = "";

    [ObservableProperty]
    public partial bool SearchFailed { get; private set; }

    public bool HasSearchState => SearchState.Length > 0;

    public bool HasResults => Results.Count > 0;

    /// <summary>The search a pause in the typing set off (for tests).</summary>
    internal Task Searching { get; private set; } = Task.CompletedTask;

    partial void OnTitleQueryChanged(string value)
    {
        _searchRun?.Cancel();
        _searchRun?.Dispose();
        _searchRun = null;
        string text = value.Trim();
        if (text.Length == 0)
        {
            ShowResults([]);
            SearchState = "";
            SearchFailed = false;
            Searching = Task.CompletedTask;
            return;
        }
        var run = new CancellationTokenSource();
        _searchRun = run;
        Searching = SearchAsync(text, run.Token);
    }

    /// <summary>The search again, after it failed.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task RetrySearchAsync()
    {
        string text = TitleQuery.Trim();
        if (text.Length == 0)
        {
            return Task.CompletedTask;
        }
        _searchRun?.Cancel();
        var run = new CancellationTokenSource();
        _searchRun = run;
        return Searching = SearchAsync(text, run.Token, wait: false);
    }

    private async Task SearchAsync(string text, CancellationToken token, bool wait = true)
    {
        try
        {
            if (wait)
            {
                await Task.Delay(SearchDelay, _time, token);
            }
            if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
            {
                return;
            }
            SearchState = "Searching…";
            SearchFailed = false;
            var images = await _images.GetAsync(server, ImageSizes.Default, token);
            var found = await _catalog.BrowseAsync(server.BaseUri, new CatalogQuery { Q = text, Limit = ResultLimit, Images = images }, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            ShowResults([.. found.Items.Where(i => i.ContentId.Length > 0).DistinctBy(i => i.ContentId).Select(item => new CollectionSearchResult(
                item.ContentId, item.Title, CollectionEditorText.TitleMeta(item.Year, item.Type), ArtworkUrl.Resolve(server.BaseUri, item.PosterUrl),
                Titles.Any(t => t.Id == item.ContentId), AddTitleCommand))]);
            SearchState = Results.Count == 0 ? "No titles match." : "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another search took over.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not search titles for a collection");
                ShowResults([]);
                SearchFailed = true;
                SearchState = "The search didn't work.";
            }
        }
    }

    private void ShowResults(IReadOnlyList<CollectionSearchResult> results)
    {
        Results.Clear();
        foreach (var result in results)
        {
            Results.Add(result);
        }
        OnPropertyChanged(nameof(HasResults));
    }

    // A title added or removed: its result says so.
    private void MarkResults()
    {
        var ids = Titles.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var result in Results)
        {
            result.IsIn = ids.Contains(result.Id);
        }
    }
}
