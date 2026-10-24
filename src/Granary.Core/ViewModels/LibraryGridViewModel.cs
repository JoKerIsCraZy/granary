using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// A poster grid that pages one catalog listing by cursor (docs/api-contract.md, 7.4). <see cref="StartAsync"/>
/// starts a listing; its query carries the image parameters, which every later page keeps because cursors are
/// bound to them. <see cref="LoadMoreCommand"/> adds the next page, never two at once, until the last one.
/// </summary>
public sealed partial class LibraryGridViewModel(CatalogApi catalog, ILogger logger) : ObservableObject
{
    private Uri? _server;
    private CatalogQuery? _query;
    private string? _nextCursor;
    private bool _totalExact = true;
    private bool _restartedForCursor;
    private CancellationTokenSource _run = new();
    private Task? _pageLoad;

    public ObservableCollection<MediaCard> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsEmpty), nameof(HasError), nameof(TotalText))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    /// <summary>Titles in the whole listing, as the first page reported.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    public partial int Total { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>A later page failed: the loaded cards stay, and <see cref="RetryLoadMoreCommand"/> asks again.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasLoadMoreError { get; private set; }

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsReady => State == LibraryLoadState.Ready;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    /// <summary>Whether a page follows the loaded ones.</summary>
    public bool HasMore => _nextCursor is not null;

    /// <summary>"1,234 titles" once the first page arrived; "About …" when the server only estimated.</summary>
    public string TotalText => State is LibraryLoadState.Ready or LibraryLoadState.Empty
        ? (_totalExact ? "" : "About ") + Total.ToString("N0", CultureInfo.InvariantCulture) + (Total == 1 ? " title" : " titles")
        : "";

    /// <summary>Raised when a listing starts over, so the view can scroll back to its top.</summary>
    public event EventHandler? Restarted;

    /// <summary>Drops the current listing and loads the first page of <paramref name="query"/>.</summary>
    public Task StartAsync(Uri server, CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(query);
        _restartedForCursor = false;
        return BeginAsync(server, query);
    }

    /// <summary>Stops the listing and forgets its cards.</summary>
    public void Reset()
    {
        Restart();
        _server = null;
        _query = null;
        State = LibraryLoadState.Idle;
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync()
    {
        // The grid asks again for every card it realizes near the end: join the page already on its way.
        if (_pageLoad is { IsCompleted: false } running)
        {
            return running;
        }
        if (!CanLoadMore())
        {
            return Task.CompletedTask;
        }
        return _pageLoad = LoadNextPageAsync();
    }

    [RelayCommand]
    private Task RetryLoadMoreAsync()
    {
        HasLoadMoreError = false;
        return LoadMoreAsync();
    }

    /// <summary>Loads the first page again after it failed.</summary>
    [RelayCommand]
    private Task RetryAsync() => _server is { } server && _query is { } query ? StartAsync(server, query) : Task.CompletedTask;

    private bool CanLoadMore() => State == LibraryLoadState.Ready && _nextCursor is not null && !IsLoadingMore && !HasLoadMoreError;

    private async Task BeginAsync(Uri server, CatalogQuery query)
    {
        var run = Restart();
        _server = server;
        _query = query;
        State = LibraryLoadState.Loading;
        Restarted?.Invoke(this, EventArgs.Empty);
        await LoadPageAsync(server, query, cursor: null, run.Token);
    }

    private CancellationTokenSource Restart()
    {
        _run.Cancel();
        _run.Dispose();
        _run = new CancellationTokenSource();
        _pageLoad = null;
        _nextCursor = null;
        _totalExact = true;
        Cards.Clear();
        Total = 0;
        ErrorMessage = "";
        IsLoadingMore = false;
        HasLoadMoreError = false;
        return _run;
    }

    private async Task LoadNextPageAsync()
    {
        var token = _run.Token;
        IsLoadingMore = true;
        try
        {
            await LoadPageAsync(_server!, _query!, _nextCursor, token);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingMore = false;
            }
        }
    }

    private async Task LoadPageAsync(Uri server, CatalogQuery query, string? cursor, CancellationToken cancellationToken)
    {
        try
        {
            var page = await catalog.BrowseAsync(server, query.WithCursor(cursor), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            Show(page, server, isFirstPage: cursor is null);
        }
        catch (SiloApiException ex) when (cursor is not null && ex.Code == ProblemCodes.InvalidCursor
            && !_restartedForCursor && !cancellationToken.IsCancellationRequested)
        {
            // The listing changed under its cursor: start over from page one, once.
            logger.LogInformation("A catalog cursor was refused; the listing starts over");
            _restartedForCursor = true;
            await BeginAsync(server, query);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Could not load a page of a library grid");
            if (cursor is null)
            {
                ErrorMessage = LibraryErrors.Describe(ex);
                State = LibraryLoadState.Error;
            }
            else
            {
                HasLoadMoreError = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer listing replaced this one.
        }
    }

    private void Show(CatalogBrowseCollection page, Uri server, bool isFirstPage)
    {
        foreach (var item in page.Items)
        {
            Cards.Add(MediaCardFactory.Poster(item, server));
        }
        _nextCursor = page.NextCursor;
        if (isFirstPage)
        {
            _totalExact = page.TotalExact;
            Total = Math.Max(page.Total, page.Items.Count);
        }
        State = Cards.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(TotalText));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }
}
