using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Settings.Search;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// The search box at the top of the settings' sidebar, as the web's SettingsSearchInput and UserSettingsSidebar: typing
/// narrows the sidebar to the pages that match (<see cref="SettingsSearch"/>), the line under the box counts them, and a page
/// opened from it can show the first of its settings that matched. The pages are read when the first word is typed; pages
/// that cannot be read leave the search finding nothing, not failing at every key.
/// </summary>
public sealed partial class SettingsSearchViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<SettingsSearchGroup>> _index;
    private readonly ILogger _logger;
    private IReadOnlyList<SettingsSearchGroup>? _groups;

    /// <param name="index">The sidebar's groups with what each page shows (<see cref="SettingsSearchIndex"/>).</param>
    public SettingsSearchViewModel(Func<IReadOnlyList<SettingsSearchGroup>> index, ILogger<SettingsSearchViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>"No matching settings", under the box and in place of the pages.</summary>
    public static string NoMatchesText => AppText.Get("web.settings.search.noMatches");

    /// <summary>What is typed in the box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    public partial string Query { get; set; } = "";

    /// <summary>Something besides spaces is typed: the sidebar shows <see cref="Results"/> and the box its clear button.</summary>
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    /// <summary>The groups with the pages that match; null while nothing is typed, when the sidebar shows every page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoMatches))]
    public partial IReadOnlyList<SettingsSearchGroup>? Results { get; private set; }

    /// <summary>
    /// The line under the box: "3 matches" or "No matching settings" while a query narrows the pages, and nothing while the box
    /// is empty, as the web keeps an untouched box quiet. It changes only when its words do, so it can be read out then.
    /// </summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>Nothing fits the query.</summary>
    public bool ShowsNoMatches => Results is { Count: 0 };

    /// <summary>The settings of a page that match the query one by one, in the page's order; none without a query.</summary>
    public IReadOnlyList<SettingsSearchEntry> MatchesOn(string tag) =>
        Results?.SelectMany(group => group.Pages).FirstOrDefault(page => page.Tag == tag) is { } page
            ? SettingsSearch.FilterEntries(page.Settings, Query)
            : [];

    [RelayCommand]
    private void Clear() => Query = "";

    // Read once: a failure is kept as no pages, so the next key does not read them again.
    private IReadOnlyList<SettingsSearchGroup> ReadIndex()
    {
        try
        {
            return _index();
        }
        catch (Exception ex)
        {
            // By its kind only: a message may name a folder of the Windows user's.
            _logger.LogWarning("The settings' search could not read the pages ({Failure})", ex.GetType().Name);
            return [];
        }
    }

    partial void OnQueryChanged(string value)
    {
        if (!HasQuery)
        {
            Results = null;
            Status = "";
            return;
        }
        _groups ??= ReadIndex();
        var results = SettingsSearch.FilterGroups(_groups, value);
        int count = SettingsSearch.CountPages(results);
        Results = results;
        Status = count == 0
            ? NoMatchesText
            : AppText.Get("web.settings.search.matchCount", ("count", count), ("count_text", AppText.Number(count)));
    }
}
