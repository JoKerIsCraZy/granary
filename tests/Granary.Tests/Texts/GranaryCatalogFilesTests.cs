using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// Granary's own texts in every language Silo's web ships (Catalogs/granary/&lt;tag&gt;.json): each translation holds
/// every English key in the English order, without the translators' notes, and each message reads, uses the English
/// message's arguments, tags and select options, and only the plural categories its language has. No text calls the
/// app Silo: the name stands for the server or the project only (docs/architecture.md, "Texts").
/// </summary>
public sealed partial class GranaryCatalogFilesTests
{
    private const string Catalogs = "src/Granary.Core/Texts/Catalogs/granary";

    /// <summary>
    /// The English texts that name Silo other than as "… Silo server" or "Silo's …", each for the server software or
    /// the project, never for Granary.
    /// </summary>
    private static readonly Dictionary<string, string> SiloAsTheProject = new(StringComparer.Ordinal)
    {
        ["granary.addServer.lead"] = "the address you use for Silo: the server",
        ["granary.addServer.updateRequired"] = "an older Silo release: the server software",
        ["granary.auth.tagline"] = "a Silo client: Granary is one",
        ["granary.changePassword.noPassword"] = "a Silo password: the server's own sign-in",
        ["granary.requests.statusFailed"] = "Silo or the download automation: the server",
        ["granary.settings.about.lead"] = "a client for Silo; not an official Silo app",
        ["granary.settings.spoilers.unsupportedDescription"] = "a newer version of Silo: the server software",
    };

    public static TheoryData<string> Translations => new(AppLanguage.Shipped.Where(tag => tag != AppLanguage.English).ToArray());

    [Fact]
    public void EveryShippedLanguage_HasACatalog_AndNoOtherIsThere()
    {
        var files = Directory.EnumerateFiles(Path.Combine(RepositoryFiles.Root, Catalogs))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(AppLanguage.Shipped.Select(tag => $"{tag}.json").Order(StringComparer.Ordinal), files);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslation_HasEveryEnglishKey_InTheEnglishOrder_WithoutNotes(string tag)
    {
        var keys = Messages(tag).Select(message => message.Key).ToList();

        Assert.Equal(EnglishKeys(), keys);
    }

    /// <summary>Laid out as the English file is: two spaces a level, a line end at the end.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslation_IsIndentedByTwoSpaces_AndEndsWithALineEnd(string tag)
    {
        string text = File.ReadAllText(CatalogPath(tag)).ReplaceLineEndings("\n");

        Assert.StartsWith("{\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n}\n", text, StringComparison.Ordinal);
        Assert.All(Messages(tag), message => Assert.Contains($"\n  \"{message.Key}\": \"", text, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryMessage_Reads(string tag)
    {
        var problems = new List<string>();

        foreach (var (key, source) in Messages(tag))
        {
            try
            {
                IcuMessageParser.Parse(source);
            }
            catch (FormatException ex)
            {
                problems.Add($"{key}: {ex.Message}");
            }
        }

        Assert.Empty(problems);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryMessage_UsesTheEnglishArgumentsTagsAndOptions(string tag)
    {
        var english = Messages(AppLanguage.English).ToDictionary(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var (key, source) in Messages(tag))
        {
            var expected = Shape.Of(IcuMessageParser.Parse(english[key]));
            var actual = Shape.Of(IcuMessageParser.Parse(source));
            if (expected.Describe() != actual.Describe())
            {
                problems.Add($"{key}: {actual.Describe()}, but English has {expected.Describe()}");
            }
        }

        Assert.Empty(problems);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryPlural_TakesOnlyItsLanguagesCategories(string tag)
    {
        var categories = CategoriesOf(tag);
        var problems = new List<string>();

        foreach (var (key, source) in Messages(tag))
        {
            foreach (var (name, options) in Shape.Of(IcuMessageParser.Parse(source)).Plurals)
            {
                var strays = options.Where(option => !option.StartsWith('=') && !categories.Contains(option)).ToList();
                if (strays.Count > 0)
                {
                    problems.Add($"{key}: {name} has {string.Join(", ", strays)}");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void ThePluralCategories_AreTheLanguagesOwn()
    {
        Assert.Equal(["one", "other"], CategoriesOf("de").Order(StringComparer.Ordinal));
        Assert.Equal(["many", "one", "other"], CategoriesOf("fr").Order(StringComparer.Ordinal));
        Assert.Equal(["other"], CategoriesOf("ja"));
        Assert.Equal(["few", "many", "one", "other", "two", "zero"], CategoriesOf("ar").Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// English names Silo for the server ("your Silo server", "Silo's web") or, in the texts listed, for the project;
    /// a translation never names it more often than its English, so no text becomes one that calls the app Silo.
    /// </summary>
    [Fact]
    public void TheEnglish_NamesSiloOnlyForTheServerOrTheProject()
    {
        var strays = Messages(AppLanguage.English)
            .Where(message => SiloNotAsServer().IsMatch(message.Value) && !SiloAsTheProject.ContainsKey(message.Key))
            .Select(message => $"{message.Key}: {message.Value}")
            .ToList();

        Assert.Empty(strays);
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void NoTranslation_NamesSiloMoreOftenThanItsEnglish(string tag)
    {
        var english = Messages(AppLanguage.English).ToDictionary(StringComparer.Ordinal);

        var strays = Messages(tag)
            .Where(message => SiloName().Count(message.Value) > SiloName().Count(english[message.Key]))
            .Select(message => $"{message.Key}: {message.Value}")
            .ToList();

        Assert.Empty(strays);
    }

    /// <summary>The categories a language's counts fall in, as the formatter picks them.</summary>
    private static HashSet<string> CategoriesOf(string tag) =>
    [
        .. Enumerable.Range(0, 1001).Select(n => (decimal)n)
            .Concat([0.5m, 1.5m, 1_000_000m, 2_000_000m])
            .Select(count => PluralRules.Category(tag, count)),
    ];

    private static List<string> EnglishKeys() => [.. Messages(AppLanguage.English).Select(message => message.Key)];

    private static string CatalogPath(string tag) => Path.Combine(RepositoryFiles.Root, Catalogs, $"{tag}.json");

    /// <summary>A catalog's messages in file order, notes left out; the file must be UTF-8 without a byte order mark.</summary>
    private static List<KeyValuePair<string, string>> Messages(string tag)
    {
        byte[] bytes = File.ReadAllBytes(CatalogPath(tag));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), $"{tag}.json starts with a byte order mark");
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        using var catalog = JsonDocument.Parse(bytes);
        var members = catalog.RootElement.EnumerateObject().ToList();
        if (tag != AppLanguage.English)
        {
            Assert.DoesNotContain(members, member => member.Name.StartsWith('@'));
        }
        return
        [
            .. members
                .Where(member => !member.Name.StartsWith('@'))
                .Select(member => KeyValuePair.Create(member.Name, member.Value.GetString()!)),
        ];
    }

    [GeneratedRegex(@"\bSilo\b")]
    private static partial Regex SiloName();

    // Silo not followed by "server" or "'s": a name for something other than the server.
    [GeneratedRegex(@"\bSilo\b(?! [Ss]erver)(?!'s )")]
    private static partial Regex SiloNotAsServer();

    /// <summary>What a message uses: its values by name, its tags, and each select's and plural's options.</summary>
    private sealed class Shape
    {
        public SortedDictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        public List<string> Tags { get; } = [];

        public SortedDictionary<string, SortedSet<string>> Selects { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, SortedSet<string>> Plurals { get; } = new(StringComparer.Ordinal);

        public static Shape Of(IReadOnlyList<MessageNode> message)
        {
            var shape = new Shape();
            shape.Walk(message);
            return shape;
        }

        /// <summary>
        /// The parts a translation must keep: names with their kind, tags, select options and a plural's exact cases
        /// (its categories differ by language).
        /// </summary>
        public string Describe() =>
            string.Join("; ", Names.Select(name => $"{name.Key}:{name.Value}"))
            + " | tags " + string.Join(",", Tags.Order(StringComparer.Ordinal))
            + " | " + string.Join("; ", Selects.Select(select => $"{select.Key}({string.Join(",", select.Value)})"))
            + " | " + string.Join("; ", Plurals.Select(plural =>
                $"{plural.Key}({string.Join(",", plural.Value.Where(option => option.StartsWith('=')))})"));

        private void Walk(IReadOnlyList<MessageNode> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case ArgumentNode argument:
                        Names.TryAdd(argument.Name, "value");
                        break;
                    case NumberNode number:
                        Names.TryAdd(number.Name, "value");
                        break;
                    case SelectNode select:
                        Names[select.Name] = "select";
                        Add(Selects, select.Name, select.Options);
                        break;
                    case PluralNode plural:
                        Names[plural.Name] = "plural";
                        Add(Plurals, plural.Name, plural.Options);
                        break;
                    case TagNode tag:
                        Tags.Add(tag.Name);
                        Walk(tag.Children);
                        break;
                }
            }
        }

        private void Add(SortedDictionary<string, SortedSet<string>> into, string name,
            IReadOnlyDictionary<string, IReadOnlyList<MessageNode>> options)
        {
            if (!into.TryGetValue(name, out var set))
            {
                into[name] = set = new SortedSet<string>(StringComparer.Ordinal);
            }
            set.UnionWith(options.Keys);
            foreach (var branch in options.Values)
            {
                Walk(branch);
            }
        }
    }
}
