using System.Text;
using System.Text.Json;

namespace Granary.Tests.Texts;

/// <summary>
/// The web catalogs scripts/sync-web-texts.ps1 writes (docs/release.md, "Translations"), read as plain JSON, without
/// the text engine: every listed key has an English message, each file holds only listed keys, and every message is a
/// shape of the web's compiled messages, so a sync that brings anything else fails here.
/// </summary>
public sealed class WebCatalogFilesTests
{
    private const string Catalogs = "src/Granary.Core/Texts/Catalogs";

    /// <summary>The languages Silo's web ships (silo-server <c>web/src/lib/appLocale.ts</c>, <c>SHIPPED_LANGUAGES</c>).</summary>
    private static readonly string[] Tags = ["en", "de", "es", "fr", "pt-BR", "ja", "zh-Hans", "ar"];

    // The element types of a compiled FormatJS message.
    private const int Literal = 0;
    private const int Argument = 1;
    private const int Number = 2;
    private const int Select = 5;
    private const int Plural = 6;
    private const int Pound = 7;
    private const int Tag = 8;

    private static readonly string[] PluralCategories = ["zero", "one", "two", "few", "many", "other"];

    public static TheoryData<string> Languages => new(Tags);

    [Fact]
    public void EveryLanguage_HasACatalog_AndNoOtherIsThere()
    {
        var files = Directory.EnumerateFiles(Path.Combine(RepositoryFiles.Root, Catalogs, "web"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(Tags.Select(tag => $"{tag}.json").Order(StringComparer.Ordinal), files);
    }

    [Fact]
    public void TheKeyList_IsInOrdinalOrder_WithEachKeyOnce()
    {
        var keys = ListedKeys();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Equal(key.Trim(), key));
        Assert.All(keys.Zip(keys.Skip(1)), pair => Assert.True(string.CompareOrdinal(pair.First, pair.Second) < 0,
            $"{pair.Second} should come before {pair.First}"));
    }

    [Fact]
    public void EveryListedKey_HasAnEnglishMessage()
    {
        using var english = Catalog("en");

        var missing = ListedKeys().Where(key => !english.RootElement.TryGetProperty(key, out _)).ToList();

        Assert.True(missing.Count == 0, $"en.json has no message for {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ACatalog_HoldsOnlyListedKeys_EachOnce_InOrdinalOrder(string tag)
    {
        using var catalog = Catalog(tag);
        var keys = catalog.RootElement.EnumerateObject().Select(member => member.Name).ToList();

        Assert.NotEmpty(keys);
        Assert.Empty(keys.Except(ListedKeys(), StringComparer.Ordinal));
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    /// <summary>As the script writes it and as upstream is laid out: two spaces a level, a line end at the end.</summary>
    [Theory]
    [MemberData(nameof(Languages))]
    public void ACatalog_IsIndentedByTwoSpaces_AndEndsWithALineEnd(string tag)
    {
        using var catalog = Catalog(tag);
        string text = File.ReadAllText(CatalogPath(tag)).ReplaceLineEndings("\n");

        Assert.StartsWith("{\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n}\n", text, StringComparison.Ordinal);
        Assert.All(catalog.RootElement.EnumerateObject(),
            member => Assert.Contains($"\n  \"{member.Name}\": ", text, StringComparison.Ordinal));
        Assert.All(text.Split('\n'), line => Assert.Equal(0, (line.Length - line.TrimStart(' ').Length) % 2));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryMessage_HasAKnownShape(string tag)
    {
        using var catalog = Catalog(tag);
        var problems = new List<string>();

        foreach (var member in catalog.RootElement.EnumerateObject())
        {
            CheckMessage(member.Value, member.Name, inPlural: false, problems);
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void TheLock_PinsACommitAndEachLanguage()
    {
        using var lockFile = Lock();
        var web = lockFile.RootElement.GetProperty("web");

        Assert.True(IsLowerHex(web.GetProperty("commit").GetString(), 40));
        var pinned = web.GetProperty("sha256").EnumerateObject().ToList();
        Assert.Equal(Tags.Order(StringComparer.Ordinal), pinned.Select(file => file.Name).Order(StringComparer.Ordinal));
        Assert.All(pinned, file => Assert.True(IsLowerHex(file.Value.GetString(), 64), $"the SHA-256 of {file.Name}"));
    }

    /// <summary>The script moves the pin but not the notices, so a new pin fails here until they name it.</summary>
    [Fact]
    public void TheNotices_NameThePinnedCommit()
    {
        using var lockFile = Lock();
        string commit = lockFile.RootElement.GetProperty("web").GetProperty("commit").GetString()!;

        Assert.Contains($"`{commit}`", File.ReadAllText(Path.Combine(RepositoryFiles.Root, "THIRD_PARTY_NOTICES.md")),
            StringComparison.Ordinal);
    }

    private static JsonDocument Lock() => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryFiles.Root, "texts.lock.json")));

    /// <summary>The key lines of web-keys.txt, as written, without its blank lines and comments.</summary>
    private static List<string> ListedKeys() =>
        File.ReadAllLines(Path.Combine(RepositoryFiles.Root, Catalogs, "web-keys.txt"))
            .Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))
            .ToList();

    private static string CatalogPath(string tag) => Path.Combine(RepositoryFiles.Root, Catalogs, "web", $"{tag}.json");

    /// <summary>A language's catalog, which must be UTF-8 without a byte order mark.</summary>
    private static JsonDocument Catalog(string tag)
    {
        byte[] bytes = File.ReadAllBytes(CatalogPath(tag));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), $"{tag}.json starts with a byte order mark");
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        var catalog = JsonDocument.Parse(bytes);
        Assert.Equal(JsonValueKind.Object, catalog.RootElement.ValueKind);
        return catalog;
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(char.IsAsciiHexDigitLower);

    private static void CheckMessage(JsonElement message, string where, bool inPlural, List<string> problems)
    {
        if (message.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{where}: not a list of elements");
            return;
        }
        int index = 0;
        foreach (var element in message.EnumerateArray())
        {
            CheckElement(element, $"{where}[{index++}]", inPlural, problems);
        }
    }

    private static void CheckElement(JsonElement element, string where, bool inPlural, List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out int kind))
        {
            problems.Add($"{where}: not an element with a type");
            return;
        }
        string members = string.Join(",", element.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        string? shape = kind switch
        {
            Literal or Argument => "type,value",
            Number => "style,type,value",
            Select => "options,type,value",
            Plural => "offset,options,pluralType,type,value",
            Pound => "type",
            Tag => "children,type,value",
            _ => null,
        };
        if (shape is null || members != shape)
        {
            problems.Add($"{where}: type {kind} with {members}");
            return;
        }
        if (kind != Pound && element.GetProperty("value").ValueKind != JsonValueKind.String)
        {
            problems.Add($"{where}: a value that is not text");
        }
        switch (kind)
        {
            case Number when element.GetProperty("style").ValueKind is not (JsonValueKind.Null or JsonValueKind.String):
                problems.Add($"{where}: a number style that is not text");
                break;
            case Pound when !inPlural:
                problems.Add($"{where}: # outside a plural");
                break;
            case Tag:
                CheckMessage(element.GetProperty("children"), $"{where}.children", inPlural, problems);
                break;
            case Select:
                CheckOptions(element, where, isPlural: false, inPlural, problems);
                break;
            case Plural:
                if (element.GetProperty("offset").ValueKind != JsonValueKind.Number
                    || element.GetProperty("pluralType").GetString() is not ("cardinal" or "ordinal"))
                {
                    problems.Add($"{where}: a plural's offset or type");
                }
                CheckOptions(element, where, isPlural: true, inPlural, problems);
                break;
        }
    }

    /// <summary>A select's or plural's options: an "other" case, and for a plural only CLDR categories and =N.</summary>
    private static void CheckOptions(JsonElement element, string where, bool isPlural, bool inPlural, List<string> problems)
    {
        var options = element.GetProperty("options");
        if (options.ValueKind != JsonValueKind.Object || !options.TryGetProperty("other", out _))
        {
            problems.Add($"{where}: options without an other case");
            return;
        }
        foreach (var option in options.EnumerateObject())
        {
            bool exact = option.Name.Length > 1 && option.Name[0] == '=' && option.Name.Skip(1).All(char.IsAsciiDigit);
            if (isPlural && !exact && !PluralCategories.Contains(option.Name))
            {
                problems.Add($"{where}: the plural case {option.Name}");
            }
            if (option.Value.ValueKind != JsonValueKind.Object
                || string.Join(",", option.Value.EnumerateObject().Select(member => member.Name)) != "value")
            {
                problems.Add($"{where}.{option.Name}: not an option with a value");
                continue;
            }
            CheckMessage(option.Value.GetProperty("value"), $"{where}.{option.Name}", inPlural || isPlural, problems);
        }
    }
}
