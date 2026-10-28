using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Collections;

// A smart collection: the Rules card instead of Titles, and under it the live preview of the titles the rules match,
// read again a short pause after the rules stop changing (the web's CollectionPreviewPane).
public sealed partial class CollectionEditorViewModel
{
    public static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);

    private IReadOnlyList<Library> _libraries = [];
    private CancellationTokenSource? _previewRun;
    private string? _previewed;
    // The Rules card was built for this visit; after that it is rebuilt only when the rules differ.
    private bool _rulesShown;

    /// <summary>A smart collection's rules, libraries and order.</summary>
    public SmartRulesViewModel Rules { get; } = new();

    /// <summary>Manual or Smart; fixed once the collection exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSmart), nameof(IsManualKind), nameof(KindLabel))]
    public partial string Kind { get; private set; } = PersonalCollectionTypes.Manual;

    public bool IsSmart => Kind == PersonalCollectionTypes.Smart;

    public bool IsManualKind => !IsSmart;

    /// <summary>The kind's tag: "Manual" or "Smart".</summary>
    public string KindLabel => IsSmart ? "Smart" : "Manual";

    /// <summary>The first titles the rules match.</summary>
    public ObservableCollection<CollectionPreviewTile> PreviewTiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewLoading), nameof(IsPreviewReady), nameof(HasPreviewTiles), nameof(IsPreviewEmpty), nameof(HasPreviewFailed))]
    public partial LibraryLoadState PreviewState { get; private set; }

    /// <summary>The rules changed since the titles shown: they stay, dimmed, until the new ones come.</summary>
    [ObservableProperty]
    public partial bool IsPreviewRefreshing { get; private set; }

    /// <summary>"40 titles match".</summary>
    [ObservableProperty]
    public partial string PreviewCount { get; private set; } = "";

    /// <summary>"showing 24" when more match than show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewShowing))]
    public partial string PreviewShowing { get; private set; } = "";

    public bool IsPreviewLoading => PreviewState == LibraryLoadState.Loading;

    public bool IsPreviewReady => PreviewState is LibraryLoadState.Ready or LibraryLoadState.Empty;

    public bool HasPreviewTiles => PreviewState == LibraryLoadState.Ready;

    /// <summary>"No titles match yet" over ghost posters.</summary>
    public bool IsPreviewEmpty => PreviewState == LibraryLoadState.Empty;

    /// <summary>"The preview didn't load. You can still save."</summary>
    public bool HasPreviewFailed => PreviewState == LibraryLoadState.Error;

    public bool HasPreviewShowing => PreviewShowing.Length > 0;

    private long? _previewTotal;

    /// <summary>The preview a pause in the rules set off (for tests).</summary>
    internal Task Previewing { get; private set; } = Task.CompletedTask;

    // "1 title matches", "1,240 titles match".
    internal static string Matches(long total) => total == 1
        ? "1 title matches"
        : $"{total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} titles match";

    // The save bar's hint before Create: how many match now.
    private string SmartCreateHint() => _previewTotal switch
    {
        null => "",
        0 => "Nothing matches yet. Titles that match later join on their own.",
        { } total => $"{Matches(total)} now. New ones join on their own.",
    };

    // Libraries to pick, what the server's rules take, and the values pickers offer: read once, none of them needed.
    private async Task ReadSmartContextAsync(Uri server)
    {
        Rules.FindPeople = async (text, token) =>
            [.. (await _catalog.SearchPeopleAsync(server, text, 10, cancellationToken: token)).Items.Select(p => p.Name).Where(n => n.Length > 0).Distinct()];
        var capabilities = Try(_catalog.GetSearchCapabilitiesAsync(server), "Could not read which rules the server takes");
        var libraries = Try(_visibleLibraries.ListAsync(server), "Could not read the profile's libraries");
        if (await capabilities is { } found)
        {
            Rules.ExtendedRules = found.ExtendedQueryRules;
            Rules.ShowTitleRules = found.ShowTitleRules;
        }
        _libraries = (await libraries)?.Items ?? [];
        _ = ReadRuleValuesAsync(server);
    }

    private async Task ReadRuleValuesAsync(Uri server)
    {
        if (await Try(_catalog.GetFiltersAsync(server, new CatalogQuery { Source = CatalogSources.Query }, includeTechnical: true),
            "Could not read the values rules can pick") is { } values)
        {
            Rules.ShowValues(values);
        }
    }

    private async Task<T?> Try<T>(Task<T> call, string message) where T : class
    {
        try
        {
            return await call;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "{Message}", message);
            return null;
        }
    }

    partial void OnKindChanged(string value)
    {
        OnPropertyChanged(nameof(MetaLine));
        DraftChanged();
    }

    private void OnRulesChanged(object? sender, EventArgs e)
    {
        DraftChanged();
        SchedulePreview(PreviewDelay);
    }

    // The preview follows the rules once they stop changing; the last titles stay meanwhile.
    private void SchedulePreview(TimeSpan delay)
    {
        if (!IsSmart || State != LibraryLoadState.Ready)
        {
            return;
        }
        // A number or span with nothing in it yet would be refused: the titles shown stay until it is filled in.
        if (Rules.HasIncompleteRule)
        {
            CancelPreview();
            IsPreviewRefreshing = false;
            return;
        }
        var rules = Rules.ToQuery();
        string key = JsonSerializer.Serialize(rules, CollectionsJsonContext.Default.SmartQuery);
        if (key == _previewed && PreviewState != LibraryLoadState.Error)
        {
            // Back to the rules shown: a read for rules in between is no longer wanted.
            CancelPreview();
            IsPreviewRefreshing = false;
            return;
        }
        CancelPreview();
        var run = new CancellationTokenSource();
        _previewRun = run;
        if (PreviewState is LibraryLoadState.Ready or LibraryLoadState.Empty)
        {
            IsPreviewRefreshing = true;
        }
        else
        {
            PreviewState = LibraryLoadState.Loading;
        }
        Previewing = PreviewAsync(rules, key, delay, run.Token);
    }

    private void CancelPreview()
    {
        _previewRun?.Cancel();
        _previewRun?.Dispose();
        _previewRun = null;
    }

    // Another visit: no preview, and no rules, are left from the last one.
    private void ResetSmart()
    {
        CancelPreview();
        _previewed = null;
        _previewTotal = null;
        _rulesShown = false;
        PreviewTiles.Clear();
        PreviewCount = "";
        PreviewShowing = "";
        IsPreviewRefreshing = false;
        PreviewState = LibraryLoadState.Idle;
        Rules.Show(SmartQuery.Empty, SmartSortConfig.Empty, []);
        Previewing = Task.CompletedTask;
    }

    private async Task PreviewAsync(SmartQuery rules, string key, TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, token);
            }
            if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
            {
                return;
            }
            var preview = await _collections.PreviewAsync(server.BaseUri, rules, cancellationToken: token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            _previewed = key;
            _previewTotal = preview.Total;
            PreviewTiles.Clear();
            foreach (var item in preview.Items.Where(i => i.ContentId.Length > 0))
            {
                PreviewTiles.Add(new CollectionPreviewTile(item.ContentId, item.Title, ArtworkUrl.Resolve(server.BaseUri, item.PosterUrl)));
            }
            PreviewCount = Matches(preview.Total);
            PreviewShowing = PreviewTiles.Count > 0 && preview.Total > PreviewTiles.Count ? $"showing {PreviewTiles.Count}" : "";
            PreviewState = PreviewTiles.Count > 0 ? LibraryLoadState.Ready : LibraryLoadState.Empty;
            IsPreviewRefreshing = false;
            OnPropertyChanged(nameof(SaveBarHint));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Newer rules took over.
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not preview a smart collection's rules");
                ShowPreviewFailed();
            }
        }
        catch (Exception ex)
        {
            // Started without being awaited: nothing may escape, and what went wrong is logged.
            _logger.LogError(ex, "The preview of a smart collection's rules failed");
            if (!token.IsCancellationRequested)
            {
                ShowPreviewFailed();
            }
        }
    }

    private void ShowPreviewFailed()
    {
        _previewed = null;
        _previewTotal = null;
        PreviewTiles.Clear();
        PreviewCount = "";
        PreviewShowing = "";
        PreviewState = LibraryLoadState.Error;
        IsPreviewRefreshing = false;
        OnPropertyChanged(nameof(SaveBarHint));
    }
}

/// <summary>A poster of the live preview.</summary>
public sealed record CollectionPreviewTile(string Id, string Title, Uri? PosterUrl);
