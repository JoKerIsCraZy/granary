using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Settings;

namespace Granary.Core.ViewModels.Collections;

/// <summary>
/// A smart collection's Rules card, as the web's <c>SmartRulesPanel</c> with its <c>RuleBuilder</c> sentence: "Show [movies]
/// from [all my libraries] that match [all] of these:", one line per rule, more groups joined by "and" or "or", then
/// Order (sort, direction, most titles) and a saved default sort with Clear. Every change raises <see cref="Changed"/>.
/// </summary>
public sealed partial class SmartRulesViewModel : ObservableObject
{
    public static IReadOnlyList<SettingChoice> Matches { get; } = [new(SmartMatch.All, "all"), new(SmartMatch.Any, "any")];

    public static IReadOnlyList<SettingChoice> Joins { get; } = [new(SmartMatch.All, "and"), new(SmartMatch.Any, "or")];

    private bool _showing;
    private JsonElement _sortConfig = SmartSortConfig.Empty;
    private CatalogFilters? _values;

    public SmartRulesViewModel()
    {
        Scope = CollectionRuleFields.Scopes[0];
        Join = Joins[0];
        SortField = new SettingChoice("added_at", "Date Added");
        Direction = new SettingChoice("desc", "Newest first");
    }

    /// <summary>Raised after every change the viewer makes.</summary>
    public event EventHandler? Changed;

    /// <summary>The kind of titles: "all titles", "movies", ….</summary>
    [ObservableProperty]
    public partial SettingChoice Scope { get; set; }

    /// <summary>The profile's libraries to pick from; none picked is all of them.</summary>
    public ObservableCollection<LibraryPick> Libraries { get; } = [];

    /// <summary>"from" before all my libraries, "from the" before named ones.</summary>
    public string FromText => Libraries.Any(l => l.IsPicked) ? "from the" : "from";

    /// <summary>"all my libraries", or the picked libraries' names.</summary>
    public string LibrariesText => Libraries.Where(l => l.IsPicked).Select(l => l.Name).ToList() is { Count: > 0 } names
        ? CollectionEditorText.JoinNames(names)
        : "all my libraries";

    /// <summary>"library" or "libraries" after named ones.</summary>
    public string LibraryWord => Libraries.Count(l => l.IsPicked) switch
    {
        0 => "",
        1 => "library",
        _ => "libraries",
    };

    /// <summary>How the groups combine: "and" or "or".</summary>
    [ObservableProperty]
    public partial SettingChoice Join { get; set; }

    public ObservableCollection<SmartGroupViewModel> Groups { get; } = [];

    public bool HasRules => Groups.Count > 0;

    public bool HasNoRules => Groups.Count == 0;

    /// <summary>"Add an “or” group".</summary>
    public string AddGroupText => $"Add an “{(Groups.Count > 1 ? Joins.FirstOrDefault(j => j.Value == Join?.Value)?.Label ?? "and" : "or")}” group";

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
            Libraries.Clear();
            foreach (var library in libraries)
            {
                if (long.TryParse(library.Id, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
                {
                    Libraries.Add(new LibraryPick(id, library.Name, query.LibraryIds.Contains(id), LibraryPicked));
                }
            }
            // A picked library this profile no longer sees stays picked, by its number.
            foreach (long id in query.LibraryIds.Where(id => Libraries.All(l => l.Id != id)))
            {
                Libraries.Add(new LibraryPick(id, $"Library {id.ToString(CultureInfo.InvariantCulture)}", true, LibraryPicked));
            }
            Scope = CollectionRuleFields.Scopes.FirstOrDefault(s => s.Value == (query.MediaScope ?? "all")) ?? CollectionRuleFields.Scopes[0];
            Join = Joins.First(j => j.Value == query.Match);
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
        // A sort the new kind of titles does not have falls back to Date Added.
        if (Sorts.All(s => s.Value != SortField?.Value))
        {
            ShowOrder(new SmartSort("added_at", SmartSort.DefaultOrder("added_at")));
        }
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

    private void AddGroupView(SmartRuleGroup group)
    {
        var view = new SmartGroupViewModel(this, Matches.First(m => m.Value == group.Match), AddRuleCommand, RemoveGroupCommand);
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
            ? $"A saved default sort, {CollectionRuleFields.SortLabel(stored.Field, stored.Order)}, wins over this Order."
            : "";
        OnPropertyChanged(nameof(FromText));
        OnPropertyChanged(nameof(LibrariesText));
        OnPropertyChanged(nameof(LibraryWord));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(AddGroupText));
        if (!_showing)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static SmartRule NewRule() => new() { Field = "genre", Op = "is", Value = CollectionRuleFields.DefaultValue("genre", "is") };

    // Blank is no limit; anything but a positive whole number is none either.
    private static long? Limit(string text) =>
        long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long limit) && limit > 0 ? limit : null;
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
    [NotifyPropertyChangedFor(nameof(IsFirst), nameof(IsLater), nameof(RemoveName), nameof(MatchName))]
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

    public string RemoveName => $"Remove group {Number}";

    public string MatchName => IsFirst ? "How the rules combine" : $"How group {Number}'s rules combine";

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

    [ObservableProperty]
    public partial bool IsPicked { get; set; }

    partial void OnIsPickedChanged(bool value) => _picked?.Invoke();
}
