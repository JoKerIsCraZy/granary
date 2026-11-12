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
    /// <summary>Whether what is missing shows marked (<c>[key]</c>, <c>{name}</c>): in a debug build, for whoever converts a text.</summary>
#if DEBUG
    internal const bool MarksProblemsInThisBuild = true;
#else
    internal const bool MarksProblemsInThisBuild = false;
#endif

    private readonly TextCatalog _own;
    private readonly TextCatalog _english;
    private readonly MessageFormatter _formatter;
    private readonly MessageFormatter _englishFormatter;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _reported = new(StringComparer.Ordinal);
    private readonly bool _marksProblems;

    internal TextLanguage(TextCatalog own, TextCatalog english, ILogger? logger, bool marksProblems = MarksProblemsInThisBuild)
    {
        _own = own;
        _english = english;
        _marksProblems = marksProblems;
        _formatter = new MessageFormatter(own.Tag, marksProblems);
        _englishFormatter = ReferenceEquals(own, english) ? _formatter : new MessageFormatter(english.Tag, marksProblems);
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

    private void Report(string key, string problem)
    {
        if (_reported.TryAdd($"{key} {problem}", true))
        {
            _logger.LogWarning("Text {Key} {Problem}", key, problem);
        }
    }

    private TextOutput Missing(string key, bool keepRuns)
    {
        var output = new TextOutput(keepRuns);
        if (_marksProblems)
        {
            output.Append($"[{key}]");
        }
        return output;
    }
}
