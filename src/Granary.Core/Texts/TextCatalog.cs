using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Granary.Core.Texts;

/// <summary>
/// One language's messages, embedded in Granary.Core:
/// <list type="bullet">
/// <item>the web's, <c>Catalogs/web/&lt;tag&gt;.json</c>: Silo's compiled catalogs (web/src/locales/&lt;tag&gt;.json),
/// unchanged per key and trimmed to the keys Granary uses;</item>
/// <item>Granary's own, <c>Catalogs/granary/&lt;tag&gt;.json</c>: <c>"granary.…"</c> keys with ICU MessageFormat source,
/// and ARB-style <c>"@granary.…"</c> entries for translators, which are skipped.</item>
/// </list>
/// Either file may be missing, and a message that cannot be read is left out and named in <see cref="Problems"/>:
/// nothing here throws, so the rest of the texts still show.
/// </summary>
internal sealed class TextCatalog
{
    private const string GranaryPrefix = "granary.";

    private static readonly ConcurrentDictionary<string, Lazy<TextCatalog>> EmbeddedCatalogs = new(StringComparer.Ordinal);

    private readonly Dictionary<string, IReadOnlyList<MessageNode>> _messages;

    private TextCatalog(string tag, Dictionary<string, IReadOnlyList<MessageNode>> messages, IReadOnlyList<string> problems)
    {
        Tag = tag;
        _messages = messages;
        Problems = problems;
    }

    public string Tag { get; }

    public int Count => _messages.Count;

    /// <summary>What could not be read, one line each, for the log.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The keys of Granary's own texts.</summary>
    public IEnumerable<string> GranaryKeys => _messages.Keys.Where(key => key.StartsWith(GranaryPrefix, StringComparison.Ordinal));

    public bool Contains(string key) => _messages.ContainsKey(key);

    public bool TryGet(string key, [MaybeNullWhen(false)] out IReadOnlyList<MessageNode> message) => _messages.TryGetValue(key, out message);

    /// <summary>The catalog embedded for a language tag, read once; empty when Granary has no files for it.</summary>
    public static TextCatalog Embedded(string tag) =>
        EmbeddedCatalogs.GetOrAdd(tag, static tag => new Lazy<TextCatalog>(() => ReadEmbedded(tag))).Value;

    /// <summary>Reads a catalog from its two files, either of which may be missing.</summary>
    public static TextCatalog Read(string tag, Stream? web, Stream? granary)
    {
        var messages = new Dictionary<string, IReadOnlyList<MessageNode>>(StringComparer.Ordinal);
        var problems = new List<string>();
        if (web is not null)
        {
            ReadFile($"web/{tag}.json", web, problems, (key, value) => messages[key] = AstReader.ReadMessage(value));
        }
        if (granary is not null)
        {
            ReadFile($"granary/{tag}.json", granary, problems, (key, value) => ReadGranary(key, value, messages));
        }
        return new TextCatalog(tag, messages, problems);
    }

    // Never throws: the catalog is read once and kept, so a failure here would otherwise stay for the whole run.
    private static TextCatalog ReadEmbedded(string tag)
    {
        try
        {
            var assembly = typeof(TextCatalog).Assembly;
            using var web = assembly.GetManifestResourceStream($"Granary.Core.Texts.Catalogs.web.{tag}.json");
            using var granary = assembly.GetManifestResourceStream($"Granary.Core.Texts.Catalogs.granary.{tag}.json");
            return Read(tag, web, granary);
        }
        catch (Exception ex)
        {
            return new TextCatalog(tag, new(StringComparer.Ordinal), [$"the catalogs of {tag} could not be read: {ex.Message}"]);
        }
    }

    private static void ReadGranary(string key, JsonElement value, Dictionary<string, IReadOnlyList<MessageNode>> messages)
    {
        if (key.StartsWith('@'))
        {
            return;
        }
        // Granary's own keys only, so a text of its own never takes the place of the web's.
        if (!key.StartsWith(GranaryPrefix, StringComparison.Ordinal))
        {
            throw new FormatException($"Granary's keys start with {GranaryPrefix}");
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("a message is ICU text");
        }
        messages[key] = IcuMessageParser.Parse(value.GetString()!);
    }

    // Reads each message of a file; one that fails is named in the problems and left out.
    private static void ReadFile(string file, Stream stream, List<string> problems, Action<string, JsonElement> read)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            problems.Add($"{file} is not JSON: {ex.Message}");
            return;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{file} is not a JSON object");
                return;
            }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                try
                {
                    read(property.Name, property.Value);
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                {
                    problems.Add($"{file}: {property.Name}: {ex.Message}");
                }
            }
        }
    }
}
