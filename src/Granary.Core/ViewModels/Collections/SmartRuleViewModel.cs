using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Settings;

namespace Granary.Core.ViewModels.Collections;

/// <summary>
/// One rule of a smart collection, as the web's <c>FilterRuleRow</c>: its field, its condition and a value control that
/// fits both. A rule these controls can't show is locked: it reads "Not editable here" and stays exactly as saved until
/// removed.
/// </summary>
public sealed partial class SmartRuleViewModel : ObservableObject
{
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex InLast = new(@"^\s*(\d+)\s*([hdwmy])\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly SmartRule _rule;
    private readonly SmartRulesViewModel _owner;
    private bool _showing;

    internal SmartRuleViewModel(SmartRule rule, SmartRulesViewModel owner, ICommand removeCommand)
    {
        _rule = rule;
        _owner = owner;
        RemoveCommand = removeCommand;
        var definition = CollectionRuleFields.Find(rule.Field);
        IsLocked = definition is null || !CanEdit(rule, definition);
        if (!IsLocked)
        {
            Show(rule.Field, rule.Op, rule.Value);
        }
    }

    /// <summary>Takes this rule.</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>The rule is kept as saved: these controls can't show it.</summary>
    public bool IsLocked { get; }

    public bool IsEditable => !IsLocked;

    /// <summary>"genre is_not \"Horror\"" for a locked rule.</summary>
    public string LockedText => $"{_rule.Field} {_rule.Op} {_rule.Value.GetRawText()}";

    /// <summary>The fields offered: the server's for this kind of title, and the rule's own if it is not among them.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Fields { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice? Field { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Operators { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice? Operator { get; set; }

    /// <summary>Text, a number, a date, or a person's or facet's name.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string From { get; set; } = "";

    [ObservableProperty]
    public partial string To { get; set; } = "";

    [ObservableProperty]
    public partial string InLastCount { get; set; } = "";

    [ObservableProperty]
    public partial SettingChoice InLastUnit { get; set; } = CollectionRuleFields.InLastUnits[0];

    /// <summary>The picked option of a select, a language, or Yes/No.</summary>
    [ObservableProperty]
    public partial SettingChoice? Choice { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Options { get; private set; } = [];

    /// <summary>What a facet or person box suggests for the text typed.</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsText), nameof(ShowsRange), nameof(ShowsChoice), nameof(ShowsSuggest), nameof(ShowsInLast), nameof(HasUnit))]
    public partial RuleShape Shape { get; private set; }

    public bool ShowsText => Shape is RuleShape.Text or RuleShape.Number or RuleShape.Date;

    public bool ShowsRange => Shape == RuleShape.Range;

    public bool ShowsChoice => Shape == RuleShape.Choice;

    public bool ShowsSuggest => Shape == RuleShape.Suggest;

    public bool ShowsInLast => Shape == RuleShape.InLast;

    /// <summary>"min", "kbps" after a number.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnit))]
    public partial string Unit { get; private set; } = "";

    public bool HasUnit => Unit.Length > 0 && Shape is RuleShape.Number or RuleShape.Range;

    /// <summary>"Value", "YYYY-MM-DD", "Pick a genre".</summary>
    [ObservableProperty]
    public partial string Placeholder { get; private set; } = "Value";

    internal RuleField? Definition => Field is { } picked ? CollectionRuleFields.Find(picked.Value) : null;

    /// <summary>The rule to save; a locked one as it came.</summary>
    public SmartRule ToRule() => IsLocked || Field is null || Operator is null
        ? _rule
        : _rule with { Field = Field.Value, Op = Operator.Value, Value = BuildValue() };

    /// <summary>The fields the rule offers changed with the kind of title or the server's capabilities.</summary>
    internal void OfferFields(IReadOnlyList<RuleField> offered)
    {
        if (IsLocked)
        {
            return;
        }
        var choices = offered.Select(f => new SettingChoice(f.Value, f.Label)).ToList();
        if (Field is { } current && choices.All(c => c.Value != current.Value) && CollectionRuleFields.Find(current.Value) is { } own)
        {
            choices.Add(new SettingChoice(own.Value, own.Label));
        }
        // A combo box clears its pick when its list is swapped: the pick is taken first.
        string? picked = Field?.Value;
        _showing = true;
        try
        {
            Fields = choices;
            Field = Fields.FirstOrDefault(c => c.Value == picked);
        }
        finally
        {
            _showing = false;
        }
    }

    /// <summary>A facet's or language's values arrived: the options or suggestions follow.</summary>
    internal void ValuesChanged()
    {
        if (Definition is { Input: RuleInput.Language } definition)
        {
            _showing = true;
            try
            {
                string? picked = Choice?.Value;
                Options = LanguageOptions(definition, picked);
                Choice = Options.FirstOrDefault(o => o.Value == picked);
            }
            finally
            {
                _showing = false;
            }
        }
    }

    /// <summary>The suggestions for <see cref="Text"/>: a facet's values that contain it, or people named so.</summary>
    internal void Suggest(IEnumerable<string> values)
    {
        Suggestions.Clear();
        foreach (string value in values)
        {
            Suggestions.Add(value);
        }
    }

    partial void OnFieldChanged(SettingChoice? oldValue, SettingChoice? newValue)
    {
        if (_showing || newValue is null || oldValue?.Value == newValue.Value)
        {
            return;
        }
        var definition = CollectionRuleFields.Find(newValue.Value);
        string op = definition?.Operators.FirstOrDefault()?.Value ?? "is";
        Show(newValue.Value, op, CollectionRuleFields.DefaultValue(newValue.Value, op));
        _owner.RuleChanged(this);
    }

    partial void OnOperatorChanged(SettingChoice? oldValue, SettingChoice? newValue)
    {
        if (_showing || newValue is null || oldValue?.Value == newValue.Value || Field is null)
        {
            return;
        }
        Show(Field.Value, newValue.Value, Normalized(Field.Value, newValue.Value, BuildValue()));
        _owner.RuleChanged(this);
    }

    partial void OnTextChanged(string value) => ValueChanged();

    partial void OnFromChanged(string value) => ValueChanged();

    partial void OnToChanged(string value) => ValueChanged();

    partial void OnInLastCountChanged(string value) => ValueChanged();

    partial void OnInLastUnitChanged(SettingChoice value) => ValueChanged();

    partial void OnChoiceChanged(SettingChoice? value) => ValueChanged();

    private void ValueChanged()
    {
        if (!_showing)
        {
            _owner.RuleChanged(this);
        }
    }

    // Shows the field, condition and value in the controls they need.
    private void Show(string field, string op, JsonElement value)
    {
        var definition = CollectionRuleFields.Find(field)!;
        _showing = true;
        try
        {
            if (Fields.All(f => f.Value != field))
            {
                Fields = [.. Fields, new SettingChoice(definition.Value, definition.Label)];
            }
            Field = Fields.First(f => f.Value == field);
            Operators = [.. definition.Operators.Where(o => !o.Hidden || o.Value == op).Select(o => new SettingChoice(o.Value, o.Label))];
            Operator = Operators.FirstOrDefault(o => o.Value == op);
            Unit = definition.Unit ?? "";
            Text = From = To = InLastCount = "";
            Choice = null;
            if (op == "between" && definition.SupportsRange)
            {
                Shape = RuleShape.Range;
                Placeholder = definition.Input == RuleInput.Date ? "YYYY-MM-DD" : "From";
                if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2)
                {
                    From = SmartValue.Display(value[0]);
                    To = SmartValue.Display(value[1]);
                }
                return;
            }
            switch (definition.Input)
            {
                case RuleInput.Boolean:
                    Shape = RuleShape.Choice;
                    Options = CollectionRuleFields.YesNo;
                    Choice = Options[value.ValueKind == JsonValueKind.True ? 0 : 1];
                    break;
                case RuleInput.Select:
                    Shape = RuleShape.Choice;
                    Options = definition.Options;
                    Choice = Options.FirstOrDefault(o => o.Value == SmartValue.Display(value));
                    break;
                case RuleInput.Language:
                    Shape = RuleShape.Choice;
                    Options = LanguageOptions(definition, SmartValue.Display(value));
                    Choice = Options.FirstOrDefault(o => o.Value == SmartValue.Display(value));
                    break;
                case RuleInput.Facet or RuleInput.Person:
                    Shape = RuleShape.Suggest;
                    Placeholder = definition.Placeholder ?? (definition.Input == RuleInput.Person ? "Search people" : "Pick a value");
                    Text = SmartValue.Display(value);
                    break;
                case RuleInput.Date when CollectionRuleFields.RelativeDateOperators.Contains(op):
                    Shape = RuleShape.InLast;
                    if (InLast.Match(SmartValue.Display(value)) is { Success: true } span)
                    {
                        InLastCount = span.Groups[1].Value;
                        InLastUnit = CollectionRuleFields.InLastUnits.FirstOrDefault(u => u.Value == span.Groups[2].Value.ToLowerInvariant())
                            ?? CollectionRuleFields.InLastUnits[0];
                    }
                    break;
                case RuleInput.Date:
                    Shape = RuleShape.Date;
                    Placeholder = "YYYY-MM-DD";
                    Text = SmartValue.Display(value);
                    break;
                default:
                    Shape = definition.Input == RuleInput.Number ? RuleShape.Number : RuleShape.Text;
                    Placeholder = "Value";
                    Text = SmartValue.Display(value);
                    break;
            }
        }
        finally
        {
            _showing = false;
        }
    }

    // The value the controls hold, typed as the field takes it: a number for number fields, text while one is empty.
    private JsonElement BuildValue()
    {
        var definition = Definition;
        if (definition is null || Operator is null)
        {
            return _rule.Value;
        }
        switch (Shape)
        {
            case RuleShape.Range:
                return SmartValue.Range(Typed(definition, From), Typed(definition, To));
            case RuleShape.Choice when definition.Input == RuleInput.Boolean:
                return SmartValue.Bool(Choice?.Value == "true");
            case RuleShape.Choice:
                return Choice is null ? SmartValue.Text("") : Typed(definition, Choice.Value);
            case RuleShape.InLast:
                return int.TryParse(InLastCount.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0
                    ? SmartValue.Text($"{count.ToString(CultureInfo.InvariantCulture)}{InLastUnit.Value}")
                    : SmartValue.Text("");
            default:
                return Typed(definition, Text);
        }
    }

    // The web's normalizeRuleValue: what a value becomes when the condition changes.
    private static JsonElement Normalized(string field, string op, JsonElement value)
    {
        var definition = CollectionRuleFields.Find(field);
        if (definition is null)
        {
            return value;
        }
        if (op == "between" && definition.SupportsRange)
        {
            return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2 ? value : CollectionRuleFields.DefaultValue(field, op);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            // Leaving "between" keeps where the range started.
            value = value.GetArrayLength() > 0 ? value[0].Clone() : CollectionRuleFields.DefaultValue(field, op);
        }
        if (definition.Input == RuleInput.Boolean)
        {
            return SmartValue.Bool(value.ValueKind == JsonValueKind.True || SmartValue.Display(value) == "true");
        }
        if (definition.Input == RuleInput.Date)
        {
            // A date and "in the last 30 days" don't convert into each other.
            string text = SmartValue.Display(value);
            return (CollectionRuleFields.RelativeDateOperators.Contains(op) ? InLast : IsoDate).IsMatch(text) ? SmartValue.Text(text) : SmartValue.Text("");
        }
        return value;
    }

    // The web's canEditRule: a field these controls know, one of its conditions, and a value that fits.
    private static bool CanEdit(SmartRule rule, RuleField definition)
    {
        if (definition.Operators.All(o => o.Value != rule.Op))
        {
            return false;
        }
        if (rule.Op == "between")
        {
            return rule.Value.ValueKind == JsonValueKind.Array && rule.Value.GetArrayLength() == 2;
        }
        return definition.Input != RuleInput.Boolean || rule.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static JsonElement Typed(RuleField definition, string text)
    {
        string trimmed = text.Trim();
        return definition.IsNumber && trimmed.Length > 0 && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? SmartValue.Number(number)
            : SmartValue.Text(definition.IsNumber ? trimmed : text);
    }

    // The languages titles have, by name; a saved one no title has any more stays listed.
    private IReadOnlyList<SettingChoice> LanguageOptions(RuleField definition, string? keep)
    {
        var codes = _owner.LanguagesFor(definition.LanguageSource ?? "original").ToList();
        if (!string.IsNullOrEmpty(keep) && !codes.Contains(keep, StringComparer.OrdinalIgnoreCase))
        {
            codes.Add(keep);
        }
        return [.. codes.Select(code => new SettingChoice(code, LanguageName(code))).OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    internal static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) ? code : culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}

/// <summary>Which value control a rule shows.</summary>
public enum RuleShape
{
    Text,
    Number,
    Date,
    Range,
    Choice,
    Suggest,
    InLast,
}
