using System.Text.RegularExpressions;
using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// Every call that gives a text its values names exactly the values its English message uses: a name with a typo would
/// leave the message's own value empty on screen, and nothing else would notice. As <see cref="TextKeysTests"/>, the
/// sources are read; a call is checked when its key and every value's name are literals, in C# (both
/// <c>AppText.Get(key, ("name", x))</c> and <c>AppText.Get(key, "name", x)</c>) and in an x:Bind.
/// </summary>
public sealed partial class TextArgumentsTests
{
    [Fact]
    public void EveryCall_NamesTheValuesItsEnglishMessageUses()
    {
        var english = TextCatalog.Embedded(AppLanguage.English);
        var calls = Calls().ToList();

        var wrong = calls
            .Select(call => (Call: call, Uses: english.TryGet(call.Key, out var message) ? Names(message) : null))
            .Where(found => found.Uses is not null && !found.Uses.SetEquals(found.Call.Names))
            .Select(found => $"{found.Call.Where}: {found.Call.Key} gets [{string.Join(", ", found.Call.Names.Order())}], "
                + $"uses [{string.Join(", ", found.Uses!.Order())}]")
            .ToList();

        Assert.Contains(calls, call => call.Names.Count > 0);
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("AppText.Get(\"k\")", "")]
    [InlineData("AppText.Get(\"k\", (\"name\", item.Name))", "name")]
    [InlineData("AppText.Get(\"k\", (\"count\", Count(a, b)), (\"count_text\", AppText.Number(n)))", "count,count_text")]
    [InlineData("AppText.Runs(\n    \"k\",\n    (\"title\", $\"{a, 3} {(b ? \"x, y\" : \")\")}\"))", "title")]
    [InlineData("AppText.Get(\"k\", \"count\", n)", "count")]
    [InlineData("AppText.Get(\"k\", \"count\", n, \"count_text\", text)", "count,count_text")]
    [InlineData("Text=\"{x:Bind t:AppText.Get('k', 'name', ViewModel.Name), Mode=OneWay}\"", "name")]
    [InlineData("Text=\"{x:Bind t:AppText.Get('k', 'n', Fn(A, B), 'm', ViewModel.M)}\"", "m,n")]
    public void ACallsNames_AreRead(string code, string names)
    {
        var call = Assert.Single(CallsIn(code, "Test.cs"));

        Assert.Equal("k", call.Key);
        Assert.Equal(names.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(), call.Names.Order());
    }

    [Theory]
    [InlineData("AppText.Get(key, (\"name\", x))")]
    [InlineData("AppText.Get(\"k\", values)")]
    [InlineData("AppText.Get(\"k\", (name, x))")]
    [InlineData("// AppText.Get(\"k\", (\"name\", x))")]
    public void ACallWithoutLiteralNames_OrInAComment_IsNotChecked(string code)
    {
        Assert.Empty(CallsIn(code, "Test.cs"));
    }

    [Fact]
    public void AMessagesValues_AreEveryNameItPrintsOrChoosesBy()
    {
        var message = IcuMessageParser.Parse(
            "<b>{name}</b> {count, plural, one {# item} other {{count_text} items}} {kind, select, movie {{year, number}} other {}}");

        Assert.Equal(["count", "count_text", "kind", "name", "year"], Names(message).Order());
    }

    private sealed record Call(string Where, string Key, IReadOnlyList<string> Names);

    private static IEnumerable<Call> Calls() =>
        from file in RepositoryFiles.Under("src", "*.cs").Concat(RepositoryFiles.Under("src", "*.xaml"))
        from call in CallsIn(File.ReadAllText(file), RepositoryFiles.Relative(file))
        select call;

    private static IEnumerable<Call> CallsIn(string source, string file)
    {
        string code = TextKeysTests.WithoutComments(source);
        foreach (Match start in CallStart().Matches(code))
        {
            int open = start.Index + start.Length - 1;
            var arguments = Arguments(code, open);
            if (arguments.Count == 0 || Literal(arguments[0]) is not { } key || ValueNames(arguments.Skip(1).ToList()) is not { } names)
            {
                continue;
            }
            yield return new Call($"{file}:{code.AsSpan(0, start.Index).Count('\n') + 1}", key, names);
        }
    }

    // The names of the values after the key: ("name", value) tuples, or name and value one after the other.
    private static List<string>? ValueNames(List<string> values)
    {
        var names = new List<string>();
        if (values.Count > 0 && values[0].StartsWith('('))
        {
            foreach (string value in values)
            {
                var parts = value.StartsWith('(') && value.EndsWith(')') ? Arguments(value, 0) : [];
                if (parts.Count != 2 || Literal(parts[0]) is not { } name)
                {
                    return null;
                }
                names.Add(name);
            }
            return names;
        }
        if (values.Count % 2 != 0)
        {
            return null;
        }
        for (int i = 0; i < values.Count; i += 2)
        {
            if (Literal(values[i]) is not { } name)
            {
                return null;
            }
            names.Add(name);
        }
        return names;
    }

    // A plain string literal's text: "…" in C#, '…' in an x:Bind.
    private static string? Literal(string argument) =>
        PlainLiteral().Match(argument) is { Success: true } match ? match.Groups["text"].Value : null;

    // The arguments between the parenthesis at open and the one that closes it, each trimmed.
    private static List<string> Arguments(string code, int open)
    {
        var arguments = new List<string>();
        int start = open + 1;
        int depth = 0;
        for (int i = open + 1; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '"' or '\'' or '$' or '@' && LiteralEnd(code, i) is int end and > 0)
            {
                i = end - 1;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' && depth > 0)
            {
                depth--;
            }
            else if (c == ')')
            {
                string last = code[start..i].Trim();
                if (last.Length > 0 || arguments.Count > 0)
                {
                    arguments.Add(last);
                }
                return arguments;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(code[start..i].Trim());
                start = i + 1;
            }
        }
        return [];
    }

    // Where the string or character literal starting at i ends, holes of an interpolated string included; -1 if none
    // starts there. A '…' is an x:Bind's string or a C# character, which skip alike.
    private static int LiteralEnd(string code, int i)
    {
        int quote = i;
        while (quote < code.Length && code[quote] is '$' or '@')
        {
            quote++;
        }
        if (quote >= code.Length || (code[quote] != '"' && !(code[quote] == '\'' && quote == i)))
        {
            return -1;
        }
        string prefix = code[i..quote];
        bool verbatim = prefix.Contains('@');
        bool interpolated = prefix.Contains('$');
        char delimiter = code[quote];
        for (int j = quote + 1; j < code.Length; j++)
        {
            char c = code[j];
            if (c == '\\' && !verbatim)
            {
                j++;
            }
            else if (c == delimiter && verbatim && j + 1 < code.Length && code[j + 1] == delimiter)
            {
                j++;
            }
            else if (c == delimiter)
            {
                return j + 1;
            }
            else if (interpolated && c == '{' && j + 1 < code.Length && code[j + 1] == '{')
            {
                j++;
            }
            else if (interpolated && c == '{')
            {
                j = HoleEnd(code, j);
                if (j < 0)
                {
                    return -1;
                }
            }
        }
        return -1;
    }

    // The index of the brace that closes an interpolated string's hole opening at open.
    private static int HoleEnd(string code, int open)
    {
        int depth = 0;
        for (int i = open + 1; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '"' or '\'' or '$' or '@' && LiteralEnd(code, i) is int end and > 0)
            {
                i = end - 1;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' && depth > 0)
            {
                depth--;
            }
            else if (c == '}')
            {
                return i;
            }
        }
        return -1;
    }

    // Every value a message prints or chooses by, in any branch; a rich-text tag is no value.
    private static HashSet<string> Names(IReadOnlyList<MessageNode> message)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Visit(IReadOnlyList<MessageNode> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case ArgumentNode argument:
                        names.Add(argument.Name);
                        break;
                    case NumberNode number:
                        names.Add(number.Name);
                        break;
                    case SelectNode select:
                        names.Add(select.Name);
                        foreach (var branch in select.Options.Values)
                        {
                            Visit(branch);
                        }
                        break;
                    case PluralNode plural:
                        names.Add(plural.Name);
                        foreach (var branch in plural.Options.Values)
                        {
                            Visit(branch);
                        }
                        break;
                    case TagNode tag:
                        Visit(tag.Children);
                        break;
                }
            }
        }
        Visit(message);
        return names;
    }

    [GeneratedRegex("""\bAppText\s*\.\s*(?:Get|Runs)\s*\(""")]
    private static partial Regex CallStart();

    [GeneratedRegex("""^(?:"(?<text>[^"\\]*)"|'(?<text>[^'^]*)')$""")]
    private static partial Regex PlainLiteral();
}
