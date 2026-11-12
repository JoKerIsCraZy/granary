using System.Text;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Tests.Texts;

/// <summary>
/// Small catalogs for the text engine's tests. The web messages are copied unchanged from Silo's compiled catalogs,
/// <c>web/src/locales/&lt;tag&gt;.json</c> at silo-server 0e95e55b6, and trimmed to a few keys, as Granary trims them.
/// The German one leaves out <c>common.save</c>, so it falls back to English.
/// </summary>
internal static class TextFixtures
{
    public const string English = """
        {
          "common.cancel": [{"type": 0, "value": "Cancel"}],
          "common.save": [{"type": 0, "value": "Save"}],
          "player.seekInterval.choice": [{"type": 6, "value": "seconds", "options": {"one": {"value": [{"type": 7}, {"type": 0, "value": " second"}]}, "other": {"value": [{"type": 7}, {"type": 0, "value": " seconds"}]}}, "offset": 0, "pluralType": "cardinal"}],
          "home.section.becauseYouEnjoy": [{"type": 0, "value": "Because you enjoy "}, {"type": 1, "value": "genres"}],
          "web.collections.copy.inCollections": [{"type": 6, "value": "count", "options": {"=0": {"value": [{"type": 0, "value": "Not in a collection yet"}]}, "one": {"value": [{"type": 0, "value": "In "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " collection"}]}, "other": {"value": [{"type": 0, "value": "In "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " collections"}]}}, "offset": 0, "pluralType": "cardinal"}],
          "web.collections.templateGallery.listKind": [{"type": 5, "value": "kind", "options": {"movie": {"value": [{"type": 0, "value": "Movies"}]}, "other": {"value": [{"type": 0, "value": "TV"}]}}}],
          "web.audiobooks.player.narratedBy": [{"type": 0, "value": "Narrated by "}, {"type": 8, "value": "name", "children": [{"type": 1, "value": "narrator"}]}],
          "web.collections.starterPacks.newCount": [{"type": 8, "value": "b", "children": [{"type": 1, "value": "count"}]}, {"type": 0, "value": " new"}],
          "web.home.addRow.rules.groupSentence": [{"type":8,"value":"join","children":[]},{"type":0,"value":" "},{"type":8,"value":"w","children":[{"type":0,"value":"titles that match"}]},{"type":0,"value":" "},{"type":8,"value":"match","children":[]},{"type":0,"value":" "},{"type":8,"value":"w","children":[{"type":0,"value":"of these:"}]}],
          "web.collections.fields.dayOrdinal": [{"type": 5, "value": "day", "options": {"1": {"value": [{"type": 0, "value": "1st"}]}, "2": {"value": [{"type": 0, "value": "2nd"}]}, "3": {"value": [{"type": 0, "value": "3rd"}]}, "21": {"value": [{"type": 0, "value": "21st"}]}, "22": {"value": [{"type": 0, "value": "22nd"}]}, "23": {"value": [{"type": 0, "value": "23rd"}]}, "31": {"value": [{"type": 0, "value": "31st"}]}, "other": {"value": [{"type": 1, "value": "day"}, {"type": 0, "value": "th"}]}}}]
        }
        """;

    public const string German = """
        {
          "common.cancel": [{"type": 0, "value": "Abbrechen"}],
          "player.seekInterval.choice": [{"type": 6, "value": "seconds", "options": {"one": {"value": [{"type": 7}, {"type": 0, "value": " Sekunde"}]}, "other": {"value": [{"type": 7}, {"type": 0, "value": " Sekunden"}]}}, "offset": 0, "pluralType": "cardinal"}],
          "home.section.becauseYouEnjoy": [{"type": 0, "value": "Weil du "}, {"type": 1, "value": "genres"}, {"type": 0, "value": " magst"}],
          "web.collections.copy.inCollections": [{"type": 6, "value": "count", "options": {"=0": {"value": [{"type": 0, "value": "Noch in keiner Sammlung"}]}, "one": {"value": [{"type": 0, "value": "In "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " Sammlung"}]}, "other": {"value": [{"type": 0, "value": "In "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " Sammlungen"}]}}, "offset": 0, "pluralType": "cardinal"}],
          "web.collections.templateGallery.listKind": [{"type": 5, "value": "kind", "options": {"movie": {"value": [{"type": 0, "value": "Filme"}]}, "other": {"value": [{"type": 0, "value": "Serien"}]}}}],
          "web.audiobooks.player.narratedBy": [{"type": 0, "value": "Gelesen von "}, {"type": 8, "value": "name", "children": [{"type": 1, "value": "narrator"}]}],
          "web.collections.fields.dayOrdinal": [{"type": 1, "value": "day"}, {"type": 0, "value": "."}]
        }
        """;

    public const string French = """
        {
          "player.seekInterval.choice": [{"type": 6, "value": "seconds", "options": {"one": {"value": [{"type": 7}, {"type": 0, "value": " seconde"}]}, "many": {"value": [{"type": 7}, {"type": 0, "value": " de secondes"}]}, "other": {"value": [{"type": 7}, {"type": 0, "value": " secondes"}]}}, "offset": 0, "pluralType": "cardinal"}],
          "web.collections.copy.inCollections": [{"type": 6, "value": "count", "options": {"=0": {"value": [{"type": 0, "value": "Dans aucune collection pour l’instant"}]}, "one": {"value": [{"type": 0, "value": "Dans "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " collection"}]}, "many": {"value": [{"type": 0, "value": "Dans "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " de collections"}]}, "other": {"value": [{"type": 0, "value": "Dans "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " collections"}]}}, "offset": 0, "pluralType": "cardinal"}],
          "web.collections.starterPacks.newCount": [{"type": 0, "value": "Nouvelles : "}, {"type": 8, "value": "b", "children": [{"type": 1, "value": "count"}]}]
        }
        """;

    public const string Arabic = """
        {
          "common.cancel": [{"type": 0, "value": "إلغاء"}],
          "player.seekInterval.choice": [{"type": 6, "value": "seconds", "options": {"zero": {"value": [{"type": 7}, {"type": 0, "value": " ثانية"}]}, "one": {"value": [{"type": 0, "value": "ثانية واحدة"}]}, "two": {"value": [{"type": 0, "value": "ثانيتان"}]}, "few": {"value": [{"type": 7}, {"type": 0, "value": " ثوانٍ"}]}, "many": {"value": [{"type": 7}, {"type": 0, "value": " ثانية"}]}, "other": {"value": [{"type": 7}, {"type": 0, "value": " ثانية"}]}}, "offset": 0, "pluralType": "cardinal"}],
          "home.section.becauseYouEnjoy": [{"type": 0, "value": "لأنك تحب "}, {"type": 1, "value": "genres"}],
          "web.collections.copy.inCollections": [{"type": 6, "value": "count", "options": {"=0": {"value": [{"type": 0, "value": "ليس في أي مجموعة بعد"}]}, "zero": {"value": [{"type": 0, "value": "في "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " مجموعة"}]}, "one": {"value": [{"type": 0, "value": "في "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " مجموعة"}]}, "two": {"value": [{"type": 0, "value": "في مجموعتين"}]}, "few": {"value": [{"type": 0, "value": "في "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " مجموعات"}]}, "many": {"value": [{"type": 0, "value": "في "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " مجموعةً"}]}, "other": {"value": [{"type": 0, "value": "في "}, {"type": 1, "value": "count_text"}, {"type": 0, "value": " مجموعة"}]}}, "offset": 0, "pluralType": "cardinal"}],
          "web.collections.fields.dayOrdinal": [{"type": 2, "value": "day", "style": null}]
        }
        """;

    public const string Japanese = """
        {
          "player.seekInterval.choice": [{"type": 6, "value": "seconds", "options": {"other": {"value": [{"type": 7}, {"type": 0, "value": "秒"}]}}, "offset": 0, "pluralType": "cardinal"}]
        }
        """;

    /// <summary>Granary's own texts in English, as ICU MessageFormat source.</summary>
    public const string GranaryEnglish = """
        {
          "granary.test.files": "{count, plural, one {# file} other {# files}}",
          "@granary.test.files": { "description": "A count of files; metadata like this is skipped." },
          "granary.test.only": "Only in English"
        }
        """;

    public const string GranaryGerman = """
        {
          "granary.test.files": "{count, plural, one {# Datei} other {# Dateien}}"
        }
        """;

    public static TextCatalog EnglishCatalog() => Catalog("en", English, GranaryEnglish);

    /// <summary>The fixture catalog of a language, with Granary's own texts where the fixtures have them.</summary>
    public static TextCatalog Catalog(string tag) => tag switch
    {
        "en" => EnglishCatalog(),
        "de" => Catalog("de", German, GranaryGerman),
        "fr" => Catalog("fr", French, null),
        "ar" => Catalog("ar", Arabic, null),
        "ja" => Catalog("ja", Japanese, null),
        _ => Catalog(tag, null, null),
    };

    public static TextCatalog Catalog(string tag, string? web, string? granary) =>
        TextCatalog.Read(tag, Stream(web), Stream(granary));

    /// <summary>
    /// A language made of the fixture catalogs, falling back to the English fixtures. It marks what is missing as its
    /// build does, unless told.
    /// </summary>
    public static TextLanguage Language(string tag, ILogger? logger = null, bool? marksProblems = null) =>
        new(Catalog(tag), EnglishCatalog(), logger, marksProblems ?? TextLanguage.MarksProblemsInThisBuild);

    private static MemoryStream? Stream(string? json) => json is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(json));
}
