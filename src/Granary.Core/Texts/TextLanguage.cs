using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Core.Texts;

/// <summary>
/// The texts of one language, with English behind them: a message the language lacks comes in English, formatted with
/// English rules, as the web's do (web/src/i18n/format.ts, <c>formatWith</c>). A key English lacks too shows as the key
/// in brackets in a debug build and as nothing in a release, as the web shows nothing; each problem is logged once.
/// Nothing here throws, so a text never takes a page down.
/// </summary>
public sealed class TextLanguage
{
    private static readonly Dictionary<string, string> EmptyValues = [];

    private readonly TextCatalog _own;
    private readonly TextCatalog _english;
    private readonly MessageFormatter _formatter;
    private readonly MessageFormatter _englishFormatter;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _reported = new(StringComparer.Ordinal);

    internal TextLanguage(TextCatalog own, TextCatalog english, ILogger? logger)
    {
        _own = own;
        _english = english;
        _formatter = new MessageFormatter(own.Tag);
        _englishFormatter = ReferenceEquals(own, english) ? _formatter : new MessageFormatter(english.Tag);
        _logger = logger ?? NullLogger.Instance;
        Tag = own.Tag;
        IsRightToLeft = AppLanguage.IsRightToLeft(Tag);
    }

    /// <summary>The shipped language's tag, such as <c>en</c> or <c>pt-BR</c>.</summary>
    public string Tag { get; }

    /// <summary>Whether the language is written right to left: Arabic.</summary>
    public bool IsRightToLeft { get; }

    /// <summary>
    /// The texts Granary embeds for the shipped language a tag selects (<see cref="AppLanguage.Match"/>), English when it
    /// selects none. Whatever its catalogs could not read goes to the log.
    /// </summary>
    public static TextLanguage Load(string tag, ILogger? logger = null)
    {
        var english = TextCatalog.Embedded(AppLanguage.English);
        string language = AppLanguage.Match(tag) ?? AppLanguage.English;
        var own = language == AppLanguage.English ? english : TextCatalog.Embedded(language);
        var loaded = new TextLanguage(own, english, logger);
        foreach (string problem in own.Problems.Concat(ReferenceEquals(own, english) ? [] : english.Problems))
        {
            loaded._logger.LogWarning("A text could not be read: {Problem}", problem);
        }
        return loaded;
    }

    /// <summary>A message as text, its rich-text tags left out and their children kept.</summary>
    public string Get(string key, ReadOnlySpan<(string Name, object? Value)> values = default) =>
        Format(key, values, keepRuns: false).ToText();

    /// <summary>A message as runs, each with the rich-text tags around it.</summary>
    public IReadOnlyList<TextRun> Runs(string key, ReadOnlySpan<(string Name, object? Value)> values = default) =>
        Format(key, values, keepRuns: true).ToRuns();

    /// <summary>
    /// Text a Silo server sent in English with the key of its catalog message, such as a row's title and its
    /// <c>title_key</c>: this language's message when its own catalog has the key and <paramref name="values"/> fill every
    /// value the message uses; otherwise, and always in English, <paramref name="serverText"/> as sent. As the web's
    /// <c>serverTextWith</c> (web/src/i18n/format.ts), so a title the server words anew still shows in English as it is.
    /// </summary>
    public string ServerText(string? key, IReadOnlyDictionary<string, string>? values, string serverText)
    {
        if (string.IsNullOrEmpty(key) || Tag == AppLanguage.English || !_own.TryGet(key, out var message)
            || !FillsEveryValue(message, values))
        {
            return serverText;
        }
        try
        {
            (string, object?)[] filled = [.. (values ?? EmptyValues).Select(value => (value.Key, (object?)value.Value))];
            var output = new TextOutput(keepRuns: false);
            _formatter.Write(message, filled, IsRightToLeft, output);
            foreach (string problem in output.Problems)
            {
                Report(key, problem);
            }
            return output.ToText();
        }
        catch (Exception ex)
        {
            if (_reported.TryAdd($"{key} threw", true))
            {
                _logger.LogError(ex, "Text {Key} could not be formatted", key);
            }
            return serverText;
        }
    }

    private TextOutput Format(string key, ReadOnlySpan<(string Name, object? Value)> values, bool keepRuns)
    {
        var output = new TextOutput(keepRuns);
        try
        {
            if (_own.TryGet(key, out var message))
            {
                _formatter.Write(message, values, IsRightToLeft, output);
            }
            else if (_english.TryGet(key, out message))
            {
                _englishFormatter.Write(message, values, IsRightToLeft, output);
            }
            else
            {
                Report(key, "is in no catalog, not even English's");
                return Missing(key, keepRuns);
            }
        }
        catch (Exception ex)
        {
            // A text must never take its page down; the formatter is not meant to throw at all.
            if (_reported.TryAdd($"{key} threw", true))
            {
                _logger.LogError(ex, "Text {Key} could not be formatted", key);
            }
            return Missing(key, keepRuns);
        }
        foreach (string problem in output.Problems)
        {
            Report(key, problem);
        }
        return output;
    }

    // Whether every value the message prints or chooses by is text the server sent, anywhere in it: the web's
    // argumentUse, with a number's value too, which no server message has had.
    private static bool FillsEveryValue(IReadOnlyList<MessageNode> nodes, IReadOnlyDictionary<string, string>? values)
    {
        bool Has(string name) => values is not null && values.TryGetValue(name, out string? value) && value is not null;

        foreach (var node in nodes)
        {
            bool fills = node switch
            {
                ArgumentNode argument => Has(argument.Name),
                NumberNode number => Has(number.Name),
                SelectNode select => Has(select.Name) && select.Options.Values.All(branch => FillsEveryValue(branch, values)),
                PluralNode plural => Has(plural.Name) && plural.Options.Values.All(branch => FillsEveryValue(branch, values)),
                TagNode tag => FillsEveryValue(tag.Children, values),
                _ => true,
            };
            if (!fills)
            {
                return false;
            }
        }
        return true;
    }

    private void Report(string key, string problem)
    {
        if (_reported.TryAdd($"{key} {problem}", true))
        {
            _logger.LogWarning("Text {Key} {Problem}", key, problem);
        }
    }

    private static TextOutput Missing(string key, bool keepRuns)
    {
        var output = new TextOutput(keepRuns);
#if DEBUG
        output.Append($"[{key}]");
#endif
        return output;
    }
}
