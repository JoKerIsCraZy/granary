using System.Globalization;

namespace Granary.Core.Texts;

/// <summary>
/// Formats messages of one language as formatjs' intl-messageformat does on the web (web/src/i18n/format.ts): a plain
/// <c>{n}</c> prints a number without grouping, while <c>#</c> and <c>{n, number}</c> group it in the language's way;
/// a plural tries the exact value (<c>=0</c>) before the count's category less the offset; a select takes the value's
/// branch, else <c>other</c>. Values that do not fit are said in the output's problems, never thrown.
/// </summary>
internal sealed class MessageFormatter
{
    private const char FirstStrongIsolate = '\u2068';
    private const char PopDirectionalIsolate = '\u2069';

    private readonly string _tag;
    private readonly NumberFormatInfo _numbers;
    private readonly bool _groupsFromFiveDigits;

    public MessageFormatter(string tag)
    {
        _tag = tag;
        _numbers = Numbers(tag);
        // CLDR's minimumGroupingDigits 2: Spanish writes 1234 but 12.345, as Intl.NumberFormat does; .NET always groups.
        _groupsFromFiveDigits = AppLanguage.Match(tag) == "es";
    }

    /// <summary>
    /// Writes a message. With <paramref name="isolate"/>, for a right-to-left interface, every value printed as text is
    /// wrapped in Unicode isolates, as the web does (format.ts, <c>prepareValues</c>): not bare digits, which an isolate
    /// would cut off from the letters around them, nor a value a select or plural chooses by.
    /// </summary>
    public void Write(IReadOnlyList<MessageNode> message, ReadOnlySpan<(string Name, object? Value)> values, bool isolate, TextOutput output)
    {
        var call = new Call(values, isolate ? Selectors(message) : null, output);
        Write(message, call, pound: null);
    }

    private void Write(IReadOnlyList<MessageNode> nodes, Call call, decimal? pound)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case LiteralNode literal:
                    call.Output.Append(literal.Value);
                    break;
                case ArgumentNode argument:
                    call.Output.Append(Printed(argument.Name, call));
                    break;
                case NumberNode number:
                    call.Output.Append(GroupedNumber(number.Name, call));
                    break;
                case PoundNode:
                    // Only a plural's count has a #; elsewhere formatjs prints nothing.
                    call.Output.Append(pound is { } count ? Grouped(count) : "");
                    break;
                case SelectNode select:
                    Write(Branch(select, call), call, pound: null);
                    break;
                case PluralNode plural:
                    var (branch, counted) = Branch(plural, call);
                    Write(branch, call, counted);
                    break;
                case TagNode tag:
                    call.Output.Open(tag.Name);
                    Write(tag.Children, call, pound);
                    call.Output.Close();
                    break;
            }
        }
    }

    private string Printed(string name, Call call)
    {
        if (!call.TryGetValue(name, out object? value))
        {
            return Missing(name, call);
        }
        string text = value switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, _numbers),
            _ => value.ToString() ?? "",
        };
        bool isolated = call.NotIsolated is not null && !call.NotIsolated.Contains(name) && text.Length > 0 && !IsDigits(text);
        return isolated ? $"{FirstStrongIsolate}{text}{PopDirectionalIsolate}" : text;
    }

    private string GroupedNumber(string name, Call call)
    {
        if (!call.TryGetValue(name, out object? value))
        {
            return Missing(name, call);
        }
        if (TryGetNumber(value, out decimal number))
        {
            return Grouped(number);
        }
        call.Output.Problem($"has no number for {name}");
        return Convert.ToString(value, _numbers) ?? "";
    }

    private IReadOnlyList<MessageNode> Branch(SelectNode select, Call call)
    {
        if (!call.TryGetValue(select.Name, out object? value))
        {
            call.Output.Problem($"has no value for {select.Name}");
            return select.Options[PluralRules.Other];
        }
        string selector = value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            _ when TryGetNumber(value, out decimal number) => Invariant(number),
            _ => value?.ToString() ?? "",
        };
        return select.Options.TryGetValue(selector, out var branch) ? branch : select.Options[PluralRules.Other];
    }

    private (IReadOnlyList<MessageNode> Branch, decimal? Count) Branch(PluralNode plural, Call call)
    {
        if (!call.TryGetValue(plural.Name, out object? value) || !TryGetNumber(value, out decimal number))
        {
            call.Output.Problem($"has no count for {plural.Name}");
            return (plural.Options[PluralRules.Other], null);
        }
        decimal counted = number - plural.Offset;
        if (plural.Options.TryGetValue($"={Invariant(number)}", out var exact))
        {
            return (exact, counted);
        }
        string category = PluralRules.Category(_tag, counted);
        return (plural.Options.TryGetValue(category, out var branch) ? branch : plural.Options[PluralRules.Other], counted);
    }

    // Intl.NumberFormat's default: grouped, up to three decimals.
    private string Grouped(decimal number)
    {
        bool ungrouped = _groupsFromFiveDigits && Math.Abs(decimal.Truncate(Math.Round(number, 3, MidpointRounding.AwayFromZero))) < 10_000;
        return number.ToString(ungrouped ? "0.###" : "#,0.###", _numbers);
    }

    private static string Missing(string name, Call call)
    {
        call.Output.Problem($"has no value for {name}");
#if DEBUG
        return $"{{{name}}}";
#else
        return "";
#endif
    }

    private static string Invariant(decimal number) => number.ToString("0.############################", CultureInfo.InvariantCulture);

    private static bool TryGetNumber(object? value, out decimal number)
    {
        try
        {
            switch (value)
            {
                case decimal d:
                    number = d;
                    return true;
                case double or float when Convert.ToDouble(value, CultureInfo.InvariantCulture) is var real && double.IsFinite(real):
                    number = (decimal)real;
                    return true;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return true;
                case string text:
                    return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            }
        }
        catch (OverflowException)
        {
            // A double beyond decimal's range counts as no number.
        }
        number = 0;
        return false;
    }

    private static bool IsDigits(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    // The values a select or plural chooses by, anywhere in the message: they are never isolated, or no branch would match.
    private static HashSet<string> Selectors(IReadOnlyList<MessageNode> message)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddSelectors(message, names);
        return names;
    }

    private static void AddSelectors(IReadOnlyList<MessageNode> nodes, HashSet<string> names)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case SelectNode select:
                    names.Add(select.Name);
                    foreach (var branch in select.Options.Values)
                    {
                        AddSelectors(branch, names);
                    }
                    break;
                case PluralNode plural:
                    names.Add(plural.Name);
                    foreach (var branch in plural.Options.Values)
                    {
                        AddSelectors(branch, names);
                    }
                    break;
                case TagNode tag:
                    AddSelectors(tag.Children, names);
                    break;
            }
        }
    }

    // The language's grouping and decimal marks. Arabic keeps Latin ones for now: .NET writes Latin digits, and would
    // put Arabic marks between them. Its own digits follow with Arabic (the web's Arabic digits, PR #3912).
    private static NumberFormatInfo Numbers(string tag)
    {
        if (AppLanguage.Match(tag) is null or "ar")
        {
            return NumberFormatInfo.InvariantInfo;
        }
        try
        {
            return CultureInfo.GetCultureInfo(tag).NumberFormat;
        }
        catch (CultureNotFoundException)
        {
            // Only without the system's culture data (invariant globalization); a text still shows.
            return NumberFormatInfo.InvariantInfo;
        }
    }

    private readonly ref struct Call(ReadOnlySpan<(string Name, object? Value)> values, HashSet<string>? notIsolated, TextOutput output)
    {
        private readonly ReadOnlySpan<(string Name, object? Value)> _values = values;

        /// <summary>When isolating, the values never isolated; null when not isolating.</summary>
        public HashSet<string>? NotIsolated { get; } = notIsolated;

        public TextOutput Output { get; } = output;

        public bool TryGetValue(string name, out object? value)
        {
            foreach (var (candidate, candidateValue) in _values)
            {
                if (candidate == name)
                {
                    value = candidateValue;
                    return true;
                }
            }
            value = null;
            return false;
        }
    }
}
