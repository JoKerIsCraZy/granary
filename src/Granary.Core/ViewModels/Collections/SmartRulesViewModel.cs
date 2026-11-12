using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Settings;

namespace Granary.Core.ViewModels.Collections;

/// <summary>
/// A smart collection's Rules card, as the web's <c>SmartRulesPanel</c> with its <c>RuleBuilder</c> sentence: "Show [movies]
/// from [all my libraries] that match [all] of these:", one line per rule, more groups joined by "and" or "or", then
/// Order (sort, direction, most titles) and a saved default sort with Clear. Every change raises <see cref="Changed"/>.
/// </summary>
public sealed partial class SmartRulesViewModel : ObservableObject
{
    private bool _showing;
    private JsonElement _sortConfig = SmartSortConfig.Empty;
    private CatalogFilters? _values;

    public SmartRulesViewModel()
    {
        // The lists the boxes pick from, built once in the language on screen: a pick is found in its own list.
        Matches = [new(SmartMatch.All, AppText.Get("web.home.addRow.rules.matchAll")), new(SmartMatch.Any, AppText.Get("web.home.addRow.rules.matchAny"))];
        Joins = [new(SmartMatch.All, AppText.Get("web.home.addRow.rules.joinAnd")), new(SmartMatch.Any, AppText.Get("web.home.addRow.rules.joinOr"))];
        Scopes = CollectionRuleFields.Scopes;
        InLastUnits = CollectionRuleFields.InLastUnits;
        Scope = Scopes[0];
        Join = Joins[0];
        SortField = new SettingChoice("added_at", CollectionRuleFields.SortName("added_at"));
        Direction = new SettingChoice("desc", CollectionRuleFields.Directions("added_at").Desc);
    }

    /// <summary>How a group's rules combine: "all" or "any".</summary>
    public IReadOnlyList<SettingChoice> Matches { get; }

    /// <summary>How the groups combine: "and" or "or".</summary>
    public IReadOnlyList<SettingChoice> Joins { get; }

    /// <summary>The kinds of title the sentence offers.</summary>
    public IReadOnlyList<SettingChoice> Scopes { get; }

    /// <summary>Days, weeks, months and years, for "is in the last".</summary>
    public IReadOnlyList<SettingChoice> InLastUnits { get; }

    /// <summary>Raised after every change the viewer makes.</summary>
    public event EventHandler? Changed;

    /// <summary>The kind of titles: "all titles", "movies", ….</summary>
    [ObservableProperty]
    public partial SettingChoice Scope { get; set; }

    /// <summary>The profile's libraries to pick from; none picked is all of them.</summary>
    public ObservableCollection<LibraryPick> Libraries { get; } = [];

    /// <summary>
    /// "Show [kind] from [libraries] library": the sentence's words and the places of its boxes (<c>scope</c> and
    /// <c>libraries</c>), as the web's RuleBuilder words it for the libraries picked.
    /// </summary>
    public IReadOnlyList<TextRun> SentenceRuns => SentenceParts().Head;

    /// <summary>"that match [all] of these:", which the first group's line reads, with the place of its box (<c>match</c>).</summary>
    public IReadOnlyList<TextRun> MatchRuns => SentenceParts().Tail;

    /// <summary>
    /// The libraries' box: "all my libraries", one name, two, or the first and how many more, as the web's library
    /// picker sums them up.
    /// </summary>
    public string LibrariesText
    {
        get
        {
            var picked = Libraries.Where(l => l.IsPicked).ToList();
            var names = picked.Where(l => l.IsKnown).Select(l => l.Name).ToList();
            string fallback = AppText.Get("web.profiles.librarySelect.libraryFallback");
            return (picked.Count, names.Count) switch
            {
                (0, _) => AppText.Get("web.collections.copy.allMyLibrariesInSentence"),
                (1, 1) => names[0],
                (_, 0) => AppText.Get("web.profiles.libraries.count", ("count", picked.Count), ("count_text", AppText.Number(picked.Count))),
                (2, 2) => AppText.Get("web.profiles.librarySelect.pair", ("first", names[0]), ("second", names[1])),
                _ => AppText.Get("web.profiles.librarySelect.more", ("count", picked.Count - 1), ("count_text", AppText.Number(picked.Count - 1)),
                    ("name", names.Count > 0 ? names[0] : fallback)),
            };
        }
    }

    /// <summary>How the groups combine: "and" or "or".</summary>
    [ObservableProperty]
    public partial SettingChoice Join { get; set; }

    public ObservableCollection<SmartGroupViewModel> Groups { get; } = [];

    public bool HasRules => Groups.Count > 0;

    public bool HasNoRules => Groups.Count == 0;

    /// <summary>"Add an “or” group"; "and" while several groups join with "and".</summary>
    public string AddGroupText => Groups.Count > 1 && Join?.Value == SmartMatch.All
        ? AppText.Get("web.home.addRow.rules.addAndGroup")
        : AppText.Get("web.home.addRow.rules.addOrGroup");

    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Sorts { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice SortField { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Directions { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice Direction { get; set; }

    /// <summary>The most titles; blank for no limit.</summary>
    [ObservableProperty]
    public partial string LimitText { get; set; } = "";

    /// <summary>"A saved default sort, Title A–Z, wins over this Order."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStoredSort))]
    public partial string StoredSortLine { get; private set; } = "";

    public bool HasStoredSort => StoredSortLine.Length > 0;

    /// <summary>The server takes the newer rule fields.</summary>
    internal bool ExtendedRules { get; set; }

    internal bool ShowTitleRules { get; set; }

    /// <summary>Asked for the people a person rule's text names.</summary>
    internal Func<string, CancellationToken, Task<IReadOnlyList<string>>>? FindPeople { get; set; }

    /// <summary>The rules as they would be saved: the libraries, the kind, the groups, the order and the limit.</summary>
    public SmartQuery ToQuery()
    {
        var groups = Groups.Select(g => new SmartRuleGroup(g.Match?.Value ?? SmartMatch.All, [.. g.Rules.Select(r => r.ToRule())])).ToList();
        return new SmartQuery
        {
            LibraryIds = [.. Libraries.Where(l => l.IsPicked).Select(l => l.Id)],
            MediaScope = Scope.Value == "all" ? null : Scope.Value,
            Match = Join?.Value ?? SmartMatch.All,
            Groups = groups,
            Sort = new SmartSort(SortField?.Value ?? "added_at", Direction?.Value ?? SmartSort.DefaultOrder(SortField?.Value)),
            Limit = Limit(LimitText),
        };
    }

    /// <summary>The <c>sort_config</c> to save: as stored, less a default sort cleared here.</summary>
    public JsonElement SortConfig => _sortConfig;

    /// <summary>Shows saved or merged rules, and the libraries to pick from.</summary>
    public void Show(SmartQuery query, JsonElement sortConfig, IReadOnlyList<Library> libraries)
    {
        _showing = true;
        try
        {
            ShowLibraries(libraries, query.LibraryIds);
            Scope = Scopes.FirstOrDefault(s => s.Value == (query.MediaScope ?? "all")) ?? Scopes[0];
            Join = Joins.FirstOrDefault(j => j.Value == query.Match) ?? Joins[0];
            Groups.Clear();
            foreach (var group in query.Groups)
            {
                AddGroupView(group);
            }
            _sortConfig = SmartSortConfig.Of(sortConfig);
            ShowOrder(query.Sort);
            LimitText = query.Limit?.ToString(CultureInfo.InvariantCulture) ?? "";
            OfferFields();
        }
        finally
        {
            _showing = false;
        }
        Refresh();
    }

    /// <summary>A rule has a number or span with nothing in it yet.</summary>
    public bool HasIncompleteRule => Groups.SelectMany(g => g.Rules).Any(r => r.IsIncomplete);

    /// <summary>
    /// The libraries to pick from and the fields the server takes arrived: shown in place, the picks and every rule as
    /// they are, so nothing being typed is taken away.
    /// </summary>
    internal void ShowContext(IReadOnlyList<Library> libraries)
    {
        var picked = Libraries.Where(l => l.IsPicked).Select(l => l.Id).ToList();
        _showing = true;
        try
        {
            ShowLibraries(libraries, picked);
            OfferFields();
        }
        finally
        {
            _showing = false;
        }
        OnPropertyChanged(nameof(LibrariesText));
        OnPropertyChanged(nameof(SentenceRuns));
    }

    /// <summary>The facet and language values of the profile's titles, for the rules' pickers.</summary>
    internal void ShowValues(CatalogFilters values)
    {
        _values = values;
        foreach (var rule in Groups.SelectMany(g => g.Rules))
        {
            rule.ValuesChanged();
        }
    }

    /// <summary>A facet's values containing <paramref name="text"/>, for a rule's suggestions.</summary>
    internal IEnumerable<string> FacetValues(string facet, string text)
    {
        IReadOnlyList<string> values = facet switch
        {
            "genre" => _values?.Genres ?? [],
            "studio" => _values?.Studios ?? [],
            "network" => _values?.Networks ?? [],
            "country" => _values?.Countries ?? [],
            "content_rating" => _values?.ContentRatings ?? [],
            _ => [],
        };
        return values.Where(v => v.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase)).Take(50);
    }

    internal IEnumerable<string> LanguagesFor(string source) => source switch
    {
        "audio" => _values?.Technical?.AudioLanguages ?? [],
        "subtitle" => _values?.Technical?.SubtitleLanguages ?? [],
        _ => _values?.OriginalLanguages ?? [],
    };

    /// <summary>Fills a facet or person rule's suggestions for what it holds now.</summary>
    public async Task SuggestAsync(SmartRuleViewModel rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Definition is not { } definition)
        {
            return;
        }
        if (definition.Input == RuleInput.Facet)
        {
            rule.Suggest(FacetValues(definition.Facet ?? "genre", rule.Text));
        }
        else if (definition.Input == RuleInput.Person && FindPeople is { } find && rule.Text.Trim().Length > 0)
        {
            rule.Suggest(await find(rule.Text.Trim(), cancellationToken));
        }
    }

    /// <summary>A new rule (Genre is …) at the end of a group, or the first group.</summary>
    [RelayCommand]
    private void AddRule(SmartGroupViewModel? group)
    {
        if (group is null)
        {
            AddGroupView(new SmartRuleGroup(SmartMatch.All, [NewRule()]));
        }
        else
        {
            group.Rules.Add(new SmartRuleViewModel(NewRule(), this, RemoveRuleCommand));
        }
        OfferFields();
        Refresh();
    }

    [RelayCommand]
    private void RemoveRule(SmartRuleViewModel? rule)
    {
        var group = Groups.FirstOrDefault(g => rule is not null && g.Rules.Contains(rule));
        if (group is null || rule is null)
        {
            return;
        }
        group.Rules.Remove(rule);
        if (group.Rules.Count == 0)
        {
            Groups.Remove(group);
        }
        Refresh();
    }

    /// <summary>Another group; with one before it, how groups join changes nothing yet, so it joins with "or".</summary>
    [RelayCommand]
    private void AddGroup()
    {
        if (Groups.Count <= 1)
        {
            _showing = true;
            Join = Joins[1];
            _showing = false;
        }
        AddGroupView(new SmartRuleGroup(SmartMatch.All, [NewRule()]));
        OfferFields();
        Refresh();
    }

    [RelayCommand]
    private void RemoveGroup(SmartGroupViewModel? group)
    {
        if (group is not null && Groups.Remove(group))
        {
            Refresh();
        }
    }

    /// <summary>Clears the saved default sort, so the Order here applies.</summary>
    [RelayCommand]
    private void ClearStoredSort()
    {
        _sortConfig = SmartSortConfig.ClearDefaultSort(_sortConfig);
        Refresh();
    }

    partial void OnScopeChanged(SettingChoice value)
    {
        // A combo box clears its pick while its list is swapped; that is no choice.
        if (_showing || value is null)
        {
            return;
        }
        OfferFields();
        // The sorts follow the new kind of titles; one it does not have falls back to its first, in that sort's own
        // direction (the web's normalizeQuerySortForScope). A saved default sort stays, as on the web.
        var sorts = CollectionRuleFields.Sorts(value.Value, keep: "");
        ShowOrder(sorts.Any(s => s.Value == SortField?.Value) || sorts.Count == 0
            ? new SmartSort(SortField?.Value ?? "added_at", Direction?.Value ?? SmartSort.DefaultOrder(SortField?.Value))
            : new SmartSort(sorts[0].Value, SmartSort.DefaultOrder(sorts[0].Value)));
        Refresh();
    }

    partial void OnJoinChanged(SettingChoice value)
    {
        if (value is null)
        {
            return;
        }
        foreach (var group in Groups)
        {
            group.JoinChanged();
        }
        Refresh();
    }

    partial void OnSortFieldChanged(SettingChoice oldValue, SettingChoice newValue)
    {
        if (_showing || newValue is null || oldValue?.Value == newValue.Value)
        {
            return;
        }
        // A sort starts in its own direction; a saved default sort would win over it, so it goes.
        ShowOrder(new SmartSort(newValue.Value, SmartSort.DefaultOrder(newValue.Value)));
        _sortConfig = SmartSortConfig.ClearDefaultSort(_sortConfig);
        Refresh();
    }

    partial void OnDirectionChanged(SettingChoice oldValue, SettingChoice newValue)
    {
        if (_showing || newValue is null || oldValue?.Value == newValue.Value)
        {
            return;
        }
        _sortConfig = SmartSortConfig.ClearDefaultSort(_sortConfig);
        Refresh();
    }

    partial void OnLimitTextChanged(string value) => Refresh();

    internal void RuleChanged(SmartRuleViewModel rule) => Refresh();

    internal void GroupChanged() => Refresh();

    private void LibraryPicked() => Refresh();

    private void ShowLibraries(IReadOnlyList<Library> libraries, IReadOnlyList<long> picked)
    {
        Libraries.Clear();
        foreach (var library in libraries)
        {
            if (long.TryParse(library.Id, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            {
                Libraries.Add(new LibraryPick(id, library.Name, picked.Contains(id), LibraryPicked));
            }
        }
        // A picked library this profile no longer sees stays picked, by its number.
        foreach (long id in picked.Where(id => Libraries.All(l => l.Id != id)))
        {
            Libraries.Add(new LibraryPick(id, AppText.Get("web.admin.homeRows.restore.libraryFallback", ("id", id.ToString(CultureInfo.InvariantCulture))),
                true, LibraryPicked) { IsKnown = false });
        }
    }

    private void AddGroupView(SmartRuleGroup group)
    {
        var view = new SmartGroupViewModel(this, Matches.FirstOrDefault(m => m.Value == group.Match) ?? Matches[0], AddRuleCommand, RemoveGroupCommand);
        foreach (var rule in group.Rules)
        {
            view.Rules.Add(new SmartRuleViewModel(rule, this, RemoveRuleCommand));
        }
        Groups.Add(view);
    }

    private void ShowOrder(SmartSort sort)
    {
        bool showing = _showing;
        _showing = true;
        try
        {
            Sorts = CollectionRuleFields.Sorts(Scope.Value, sort.Field);
            SortField = Sorts.FirstOrDefault(s => s.Value == sort.Field) ?? new SettingChoice(sort.Field, sort.Field);
            var (desc, asc) = CollectionRuleFields.Directions(sort.Field);
            Directions = [new("desc", desc), new("asc", asc)];
            Direction = Directions.First(d => d.Value == sort.Order);
        }
        finally
        {
            _showing = showing;
        }
    }

    private void OfferFields()
    {
        var offered = CollectionRuleFields.Offered(Scope.Value, ExtendedRules, ShowTitleRules);
        foreach (var rule in Groups.SelectMany(g => g.Rules))
        {
            rule.OfferFields(offered);
        }
    }

    // Everything that reads the rules follows, then whoever listens.
    private void Refresh()
    {
        for (int i = 0; i < Groups.Count; i++)
        {
            Groups[i].Number = i + 1;
        }
        StoredSortLine = SmartSortConfig.DefaultSort(_sortConfig) is { } stored
            ? AppText.Get("web.collections.copy.storedSortLine", ("sort", CollectionRuleFields.SortLabel(stored.Field, stored.Order)))
            : "";
        OnPropertyChanged(nameof(StoredSortName));
        OnPropertyChanged(nameof(SentenceRuns));
        OnPropertyChanged(nameof(MatchRuns));
        OnPropertyChanged(nameof(LibrariesText));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(AddGroupText));
        if (!_showing)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>"Clear the saved default sort, Title A–Z", for the Clear button.</summary>
    public string StoredSortName => SmartSortConfig.DefaultSort(_sortConfig) is { } stored
        ? AppText.Get("web.collections.fields.clearStoredSort", ("sort", CollectionRuleFields.SortLabel(stored.Field, stored.Order)))
        : "";

    // The web's sentence for the libraries picked, cut where its matching clause starts its own line: the head before
    // the break, the clause after it. The clause is the one with rules, which the first group shows.
    private (IReadOnlyList<TextRun> Head, IReadOnlyList<TextRun> Tail) SentenceParts()
    {
        var picked = Libraries.Where(l => l.IsPicked).ToList();
        int count = picked.Count;
        var runs = picked.Any(l => l.IsKnown)
            ? AppText.Runs("web.home.addRow.rules.sentenceNamed", ("count", count), ("rules", "yes"))
            : count > 0
                ? AppText.Runs("web.home.addRow.rules.sentencePicked", ("rules", "yes"))
                : AppText.Runs("web.home.addRow.rules.sentenceAll", ("rules", "yes"));
        int split = runs.ToList().FindIndex(run => run.Tags.Contains("break"));
        return split < 0 ? (runs, []) : ([.. runs.Take(split)], [.. runs.Skip(split + 1)]);
    }

    private static SmartRule NewRule() => new() { Field = "genre", Op = "is", Value = CollectionRuleFields.DefaultValue("genre", "is") };

    // Blank is no limit; so is anything but a positive whole number, and the server's stand-in for none
    // (normalizeSmartCollectionLimit).
    private static long? Limit(string text) =>
        long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long limit) && limit > 0 && limit != SmartQuery.NoLimitSentinel
            ? limit
            : null;
}

/// <summary>A group of rules: its number, how its rules combine, and its rules.</summary>
public sealed partial class SmartGroupViewModel : ObservableObject
{
    private readonly SmartRulesViewModel _owner;

    internal SmartGroupViewModel(SmartRulesViewModel owner, SettingChoice match, System.Windows.Input.ICommand addRuleCommand,
        System.Windows.Input.ICommand removeCommand)
    {
        _owner = owner;
        Match = match;
        AddRuleCommand = addRuleCommand;
        RemoveCommand = removeCommand;
    }

    public ObservableCollection<SmartRuleViewModel> Rules { get; } = [];

    /// <summary>"all" or "any" of its rules.</summary>
    [ObservableProperty]
    public partial SettingChoice Match { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirst), nameof(IsLater), nameof(RemoveName), nameof(MatchName), nameof(SentenceRuns))]
    public partial int Number { get; internal set; }

    public bool IsFirst => Number == 1;

    /// <summary>A later group: its header joins it to the ones before.</summary>
    public bool IsLater => Number > 1;

    /// <summary>The groups' join, which every later group's header shows.</summary>
    public SettingChoice Join
    {
        get => _owner.Join;
        set => _owner.Join = value;
    }

    public string RemoveName => AppText.Get("web.home.addRow.rules.removeGroup", ("number", Number));

    public string MatchName => IsFirst
        ? AppText.Get("web.home.addRow.rules.howRulesCombine")
        : AppText.Get("web.home.addRow.rules.groupMatchLabel", ("number", Number));

    /// <summary>"all" or "any", for the group's box.</summary>
    public IReadOnlyList<SettingChoice> Matches => _owner.Matches;

    /// <summary>"and" or "or", for a later group's box.</summary>
    public IReadOnlyList<SettingChoice> Joins => _owner.Joins;

    /// <summary>
    /// The group's line: the first reads "that match [all] of these:" from the rules' sentence, a later one
    /// "[or] titles that match [all] of these:", each with the places of its boxes.
    /// </summary>
    public IReadOnlyList<TextRun> SentenceRuns => IsFirst ? _owner.MatchRuns : AppText.Runs("web.home.addRow.rules.groupSentence");

    /// <summary>Takes this group.</summary>
    public System.Windows.Input.ICommand AddRuleCommand { get; }

    /// <summary>Takes this group.</summary>
    public System.Windows.Input.ICommand RemoveCommand { get; }

    partial void OnMatchChanged(SettingChoice value)
    {
        if (value is not null)
        {
            _owner.GroupChanged();
        }
    }

    internal void JoinChanged() => OnPropertyChanged(nameof(Join));
}

/// <summary>A library the rules can be limited to.</summary>
public sealed partial class LibraryPick : ObservableObject
{
    private readonly Action _picked;

    internal LibraryPick(long id, string name, bool isPicked, Action picked)
    {
        Id = id;
        Name = name;
        _picked = picked;
        IsPicked = isPicked;
    }

    public long Id { get; }

    public string Name { get; }

    /// <summary>A library this profile sees, named; false for a picked one it no longer sees, known by its number.</summary>
    public bool IsKnown { get; init; } = true;

    [ObservableProperty]
    public partial bool IsPicked { get; set; }

    partial void OnIsPickedChanged(bool value) => _picked?.Invoke();
}
