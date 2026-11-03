using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Collections;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>A value the filter can match (a genre, decade, rating, quality or language), switched on or off in the flyout.</summary>
public sealed partial class LibraryFilterToggle(string value, string label) : ObservableObject
{
    public string Value { get; } = value;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// The Filters flyout of a library's grid or a list, in the web's order and words: Genres, Decade, Year From and Year To,
/// Content Rating, Video Quality, Original, Audio and Subtitle Language, and Watch Status (<see cref="LibraryFilterSet"/>).
/// The flyout edits a draft; <see cref="ApplyCommand"/> and <see cref="ClearCommand"/> commit it and restart the grid.
/// </summary>
public sealed partial class LibraryFiltersViewModel(Func<Task> applied, ILogger logger) : ObservableObject
{
    public const int MinimumYear = LibraryFilterSet.MinimumYear;
    public const int MaximumYear = LibraryFilterSet.MaximumYear;

    // The facet values last read for the scope; the chips show them and any applied value they no longer hold.
    private CatalogFilters _options = new();

    /// <summary>The Watch Status choices, as the web words them, in the order of <see cref="LibraryWatchStatus"/>.</summary>
    public static IReadOnlyList<string> WatchStatusChoices { get; } = ["Any", "Unwatched", "In Progress", "Watched"];

    /// <summary>The applied filters.</summary>
    public LibraryFilterSet Applied { get; private set; } = LibraryFilterSet.None;

    /// <summary>The count on the Filters pill (<see cref="LibraryFilterSet.Count"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial int ActiveCount { get; private set; }

    public bool HasActiveFilters => ActiveCount > 0;

    public ObservableCollection<LibraryFilterToggle> GenreOptions { get; } = [];

    /// <summary>This decade back to the 1900s, as "2020s".</summary>
    public IReadOnlyList<LibraryFilterToggle> DecadeOptions { get; } =
        [.. LibraryFilterSet.DecadesUntil(DateTime.UtcNow.Year).Select(d => new LibraryFilterToggle(Invariant(d), Invariant(d) + "s"))];

    public ObservableCollection<LibraryFilterToggle> RatingOptions { get; } = [];

    /// <summary>"4K", "HDR" and "DOVI", as the web's filter sheet labels them; Apply reads them by position.</summary>
    public IReadOnlyList<LibraryFilterToggle> VideoQualityOptions { get; } =
        [new LibraryFilterToggle("4k", "4K"), new LibraryFilterToggle("hdr", "HDR"), new LibraryFilterToggle("dolby_vision", "DOVI")];

    public ObservableCollection<LibraryFilterToggle> OriginalLanguageOptions { get; } = [];

    public ObservableCollection<LibraryFilterToggle> AudioLanguageOptions { get; } = [];

    public ObservableCollection<LibraryFilterToggle> SubtitleLanguageOptions { get; } = [];

    /// <summary>NaN while empty, as a number box reports it.</summary>
    [ObservableProperty]
    public partial double DraftYearFrom { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double DraftYearTo { get; set; } = double.NaN;

    /// <summary>An index into <see cref="WatchStatusChoices"/>.</summary>
    [ObservableProperty]
    public partial int DraftWatchStatusIndex { get; set; }

    /// <summary>The genre, rating and language lists could not be read; the other filters still work.</summary>
    [ObservableProperty]
    public partial bool HasOptionsError { get; private set; }

    public bool HasGenres => GenreOptions.Count > 0;

    public bool HasRatings => RatingOptions.Count > 0;

    public bool HasOriginalLanguages => OriginalLanguageOptions.Count > 0;

    public bool HasAudioLanguages => AudioLanguageOptions.Count > 0;

    public bool HasSubtitleLanguages => SubtitleLanguageOptions.Count > 0;

    /// <summary>The query with the applied filters.</summary>
    public CatalogQuery ApplyTo(CatalogQuery query) => Applied.ApplyTo(query);

    /// <summary>
    /// Reads the facet values of <paramref name="scope"/> for the chips, the files' audio and subtitle languages too. Those
    /// are the heavier read: when it fails, the rest is asked for without them.
    /// </summary>
    public async Task LoadOptionsAsync(CatalogApi catalog, Uri server, CatalogQuery scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            CatalogFilters filters;
            try
            {
                filters = await catalog.GetFiltersAsync(server, scope, includeTechnical: true, cancellationToken);
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex) && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not load the languages of the files; asking for the other filter options alone");
                filters = await catalog.GetFiltersAsync(server, scope, includeTechnical: false, cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            HasOptionsError = false;
            _options = filters;
            ShowOptions();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Another library took over.
                return;
            }
            logger.LogWarning(ex, "Could not load the filter options of a library");
            HasOptionsError = true;
        }
    }

    /// <summary>Forgets the applied filters and the options, for another library.</summary>
    public void Reset()
    {
        Commit(LibraryFilterSet.None);
        HasOptionsError = false;
        _options = new CatalogFilters();
        ShowOptions();
        BeginEdit();
    }

    /// <summary>
    /// Applies remembered filters without restarting anything. They are taken as Apply takes them, so a damaged file can't
    /// make a query the server refuses; a decade the flyout does not offer is left out.
    /// </summary>
    public void Restore(LibraryFilterSet filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var normalized = filters.Normalized();
        Commit(normalized with { Decades = [.. normalized.Decades.Where(d => DecadeOptions.Any(o => o.Value == Invariant(d)))] });
        ShowOptions();
        BeginEdit();
    }

    /// <summary>Fills the draft from the applied filters when the flyout opens.</summary>
    public void BeginEdit()
    {
        Select(GenreOptions, Applied.Genres);
        Select(DecadeOptions, [.. Applied.Decades.Select(Invariant)]);
        Select(RatingOptions, Applied.ContentRatings);
        VideoQualityOptions[0].IsSelected = Applied.FourK;
        VideoQualityOptions[1].IsSelected = Applied.Hdr;
        VideoQualityOptions[2].IsSelected = Applied.DolbyVision;
        Select(OriginalLanguageOptions, Applied.OriginalLanguages);
        Select(AudioLanguageOptions, Applied.AudioLanguages);
        Select(SubtitleLanguageOptions, Applied.SubtitleLanguages);
        DraftYearFrom = Applied.YearFrom ?? double.NaN;
        DraftYearTo = Applied.YearTo ?? double.NaN;
        DraftWatchStatusIndex = (int)Applied.WatchStatus;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ApplyAsync()
    {
        Commit(new LibraryFilterSet
        {
            Genres = Picked(GenreOptions),
            Decades = [.. Picked(DecadeOptions).Select(d => int.Parse(d, CultureInfo.InvariantCulture))],
            YearFrom = Year(DraftYearFrom),
            YearTo = Year(DraftYearTo),
            ContentRatings = Picked(RatingOptions),
            FourK = VideoQualityOptions[0].IsSelected,
            Hdr = VideoQualityOptions[1].IsSelected,
            DolbyVision = VideoQualityOptions[2].IsSelected,
            OriginalLanguages = Picked(OriginalLanguageOptions),
            AudioLanguages = Picked(AudioLanguageOptions),
            SubtitleLanguages = Picked(SubtitleLanguageOptions),
            WatchStatus = (LibraryWatchStatus)DraftWatchStatusIndex,
        }.Normalized());
        return applied();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ClearAsync()
    {
        ClearSilently();
        return applied();
    }

    /// <summary>Clears the filters without restarting the grid; the caller restarts it.</summary>
    internal void ClearSilently()
    {
        Commit(LibraryFilterSet.None);
        BeginEdit();
    }

    private void Commit(LibraryFilterSet filters)
    {
        Applied = filters;
        ActiveCount = filters.Count;
    }

    private void ShowOptions()
    {
        Fill(GenreOptions, _options.Genres, Applied.Genres, label: null);
        Fill(RatingOptions, _options.ContentRatings, Applied.ContentRatings, label: null);
        Fill(OriginalLanguageOptions, _options.OriginalLanguages, Applied.OriginalLanguages, SmartRuleViewModel.LanguageName);
        Fill(AudioLanguageOptions, _options.Technical?.AudioLanguages ?? [], Applied.AudioLanguages, SmartRuleViewModel.LanguageName);
        Fill(SubtitleLanguageOptions, _options.Technical?.SubtitleLanguages ?? [], Applied.SubtitleLanguages, SmartRuleViewModel.LanguageName);
        OnPropertyChanged(nameof(HasGenres));
        OnPropertyChanged(nameof(HasRatings));
        OnPropertyChanged(nameof(HasOriginalLanguages));
        OnPropertyChanged(nameof(HasAudioLanguages));
        OnPropertyChanged(nameof(HasSubtitleLanguages));
    }

    // The scope's values in the server's order, languages by name, then any applied value the scope no longer has, so it
    // can still be switched off. A value already shown keeps whether it is picked, so options that arrive while the flyout
    // is open leave the draft as it is; Restore and Reset fill the draft afresh.
    private static void Fill(ObservableCollection<LibraryFilterToggle> options, IReadOnlyList<string> offered, IReadOnlyList<string> applied,
        Func<string, string>? label)
    {
        var picked = options.GroupBy(o => o.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().IsSelected, StringComparer.Ordinal);
        var values = offered.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal)
            .Select(v => new LibraryFilterToggle(v, label?.Invoke(v) ?? v));
        if (label is not null)
        {
            values = values.OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase);
        }
        var shown = values.ToList();
        shown.AddRange(applied.Where(v => !shown.Any(t => t.Value == v)).Select(v => new LibraryFilterToggle(v, label?.Invoke(v) ?? v)));
        options.Clear();
        foreach (var option in shown)
        {
            option.IsSelected = picked.TryGetValue(option.Value, out bool isPicked) ? isPicked : applied.Contains(option.Value, StringComparer.Ordinal);
            options.Add(option);
        }
    }

    private static void Select(IEnumerable<LibraryFilterToggle> options, IReadOnlyList<string> values)
    {
        foreach (var option in options)
        {
            option.IsSelected = values.Contains(option.Value, StringComparer.Ordinal);
        }
    }

    private static string[] Picked(IEnumerable<LibraryFilterToggle> options) => [.. options.Where(o => o.IsSelected).Select(o => o.Value)];

    private static int? Year(double value) =>
        double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), MinimumYear, MaximumYear) : null;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
