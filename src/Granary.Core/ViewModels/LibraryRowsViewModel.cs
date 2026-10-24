using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The Recommended tab: the library's landing rows as on Home (docs/api-contract.md, 7.1 and 7.3). Rows
/// with <c>total_count == 0</c> are left out; rows that came back without cards but with a positive count
/// are fetched on their own, at most <see cref="MaxConcurrentHydrations"/> at once, as Android does.
/// </summary>
public sealed partial class LibraryRowsViewModel(CatalogApi catalog, INavigationService navigation, ILogger logger) : ObservableObject
{
    public const int MaxConcurrentHydrations = 4;

    private Uri? _server;
    private string? _libraryId;
    private ImageParameters _images = ImageParameters.None;
    private CancellationTokenSource _run = new();

    public ObservableCollection<LibraryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    /// <summary>Loads the rows of a library; a running load of other rows stops.</summary>
    public async Task LoadAsync(Uri server, string libraryId, ImageParameters images)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentNullException.ThrowIfNull(images);
        var token = Restart();
        (_server, _libraryId, _images) = (server, libraryId, images);
        State = LibraryLoadState.Loading;
        try
        {
            var sections = await catalog.ListLibrarySectionsAsync(server, libraryId, images, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            var empty = new List<LibraryRowViewModel>();
            foreach (var section in sections.Sections.Where(s => s.TotalCount > 0))
            {
                var row = new LibraryRowViewModel(section.Id, section.Title, OpenCardCommand, ExploreAllCommandFor(section, libraryId))
                {
                    Cards = CardsOf(section, server),
                };
                Rows.Add(row);
                if (!row.HasCards)
                {
                    empty.Add(row);
                }
            }
            ShowState(stillLoading: empty.Count > 0);
            await HydrateAsync(server, libraryId, images, empty, token);
            if (!token.IsCancellationRequested)
            {
                ShowState(stillLoading: false);
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex, token))
        {
            logger.LogWarning(ex, "Could not load the rows of a library");
            ErrorMessage = LibraryErrors.Describe(ex);
            State = LibraryLoadState.Error;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another library, or another load of this one, took over.
        }
    }

    /// <summary>Stops loading and forgets the rows.</summary>
    public void Reset()
    {
        Restart();
        _server = null;
        _libraryId = null;
        State = LibraryLoadState.Idle;
    }

    [RelayCommand]
    private Task RetryAsync() =>
        _server is { } server && _libraryId is { } libraryId ? LoadAsync(server, libraryId, _images) : Task.CompletedTask;

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null)
        {
            navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    private RelayCommand ExploreAllCommandFor(Section section, string libraryId) =>
        new(() => navigation.NavigateTo(AppRoute.Section, new SectionRouteParameter(section.Id, section.Title, libraryId)));

    private CancellationToken Restart()
    {
        _run.Cancel();
        _run.Dispose();
        _run = new CancellationTokenSource();
        Rows.Clear();
        ErrorMessage = "";
        return _run.Token;
    }

    private void ShowState(bool stillLoading)
    {
        if (Rows.Any(r => r.HasCards))
        {
            State = LibraryLoadState.Ready;
        }
        else
        {
            State = stillLoading ? LibraryLoadState.Loading : LibraryLoadState.Empty;
        }
    }

    private async Task HydrateAsync(Uri server, string libraryId, ImageParameters images, IReadOnlyList<LibraryRowViewModel> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }
        using var gate = new SemaphoreSlim(MaxConcurrentHydrations);
        await Task.WhenAll(rows.Select(row => HydrateRowAsync(server, libraryId, images, row, gate, cancellationToken)));
    }

    private async Task HydrateRowAsync(Uri server, string libraryId, ImageParameters images, LibraryRowViewModel row, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var section = await catalog.GetLibrarySectionItemsAsync(server, libraryId, row.Id, images, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                row.Cards = CardsOf(section, server);
                ShowState(stillLoading: true);
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex, cancellationToken))
        {
            // The row stays hidden; the others still show.
            logger.LogWarning(ex, "Could not load the cards of a library row");
        }
        finally
        {
            gate.Release();
        }
    }

    private static List<MediaCard> CardsOf(Section section, Uri server) =>
        [.. section.Items.Select(item => MediaCardFactory.ForSection(section, item, server))];
}
