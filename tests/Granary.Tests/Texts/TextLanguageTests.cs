using Granary.Core.Texts;
using Granary.Tests.Fakes;

namespace Granary.Tests.Texts;

/// <summary>
/// Looking texts up and formatting them, with Silo's own messages (TextFixtures): the same output the web's react-intl
/// gives for them, and English for a message a language lacks (web/src/i18n/README.md, "The API").
/// </summary>
public sealed class TextLanguageTests
{
    private const string Fsi = "\u2068";
    private const string Pdi = "\u2069";

    [Fact]
    public void ALiteral_ShowsAsWritten()
    {
        Assert.Equal("Cancel", TextFixtures.Language("en").Get("common.cancel"));
        Assert.Equal("Abbrechen", TextFixtures.Language("de").Get("common.cancel"));
    }

    [Fact]
    public void AnArgument_TakesItsValue()
    {
        var german = TextFixtures.Language("de");

        Assert.Equal("Weil du Krimis magst", german.Get("home.section.becauseYouEnjoy", [("genres", "Krimis")]));
    }

    [Fact]
    public void ANumberArgument_IsWrittenInTheLanguagesCulture_WithoutGrouping()
    {
        var english = TextFixtures.Language("en");
        var german = TextFixtures.Language("de");

        Assert.Equal("Because you enjoy 1500", english.Get("home.section.becauseYouEnjoy", [("genres", 1500)]));
        Assert.Equal("Because you enjoy 2.5", english.Get("home.section.becauseYouEnjoy", [("genres", 2.5)]));
        Assert.Equal("Weil du 2,5 magst", german.Get("home.section.becauseYouEnjoy", [("genres", 2.5)]));
    }

    [Theory]
    [InlineData("en", 1, "1 second")]
    [InlineData("en", 0, "0 seconds")]
    [InlineData("en", 30, "30 seconds")]
    [InlineData("en", 1000, "1,000 seconds")]
    [InlineData("de", 1, "1 Sekunde")]
    [InlineData("de", 1000, "1.000 Sekunden")]
    [InlineData("fr", 0, "0 seconde")]
    [InlineData("fr", 1, "1 seconde")]
    [InlineData("fr", 2, "2 secondes")]
    [InlineData("fr", 1000000, "1\u202F000\u202F000 de secondes")]
    [InlineData("ja", 1, "1秒")]
    [InlineData("ar", 0, "0 ثانية")]
    [InlineData("ar", 1, "ثانية واحدة")]
    [InlineData("ar", 2, "ثانيتان")]
    [InlineData("ar", 5, "5 ثوانٍ")]
    [InlineData("ar", 11, "11 ثانية")]
    [InlineData("ar", 100, "100 ثانية")]
    public void APlural_PicksTheLanguagesForm_AndPrintsTheCount(string tag, int seconds, string expected)
    {
        var language = TextFixtures.Language(tag);

        Assert.Equal(expected, language.Get("player.seekInterval.choice", [("seconds", seconds)]));
    }

    [Theory]
    [InlineData(0, "Not in a collection yet")]
    [InlineData(1, "In one collection")]
    [InlineData(2, "In two collections")]
    public void AnExactValue_WinsOverThePluralCategory(int count, string expected)
    {
        var english = TextFixtures.Language("en");
        string countText = count switch { 1 => "one", 2 => "two", _ => "" };

        Assert.Equal(expected, english.Get("web.collections.copy.inCollections", [("count", count), ("count_text", countText)]));
    }

    [Fact]
    public void ThePound_IsTheCountLessTheOffset()
    {
        var english = TextLanguageWith("granary.test.guests",
            "{count, plural, offset:1 =0 {Nobody} =1 {Elena} one {Elena and one other} other {Elena and # others}}");

        // An exact value matches the count itself; the category follows the count less the offset, as in formatjs.
        Assert.Equal("Nobody", english.Get("granary.test.guests", [("count", 0)]));
        Assert.Equal("Elena", english.Get("granary.test.guests", [("count", 1)]));
        Assert.Equal("Elena and one other", english.Get("granary.test.guests", [("count", 2)]));
        Assert.Equal("Elena and 3 others", english.Get("granary.test.guests", [("count", 4)]));
    }

    [Fact]
    public void APluralOfDecimals_FollowsTheVisibleFractionDigits()
    {
        var english = TextFixtures.Language("en");

        Assert.Equal("1.5 seconds", english.Get("player.seekInterval.choice", [("seconds", 1.5)]));
        Assert.Equal("1 second", english.Get("player.seekInterval.choice", [("seconds", 1.0m)]));
    }

    [Fact]
    public void APluralOfAStringNumber_CountsItsValue()
    {
        Assert.Equal("1 second", TextFixtures.Language("en").Get("player.seekInterval.choice", [("seconds", "1")]));
    }

    [Theory]
    [InlineData("movie", "Filme")]
    [InlineData("show", "Serien")]
    public void ASelect_PicksTheMatchingBranch_ElseOther(string kind, string expected)
    {
        Assert.Equal(expected, TextFixtures.Language("de").Get("web.collections.templateGallery.listKind", [("kind", kind)]));
    }

    [Theory]
    [InlineData(2, "2nd")]
    [InlineData(22, "22nd")]
    [InlineData(4, "4th")]
    public void ASelect_MatchesANumberByItsDigits(int day, string expected)
    {
        Assert.Equal(expected, TextFixtures.Language("en").Get("web.collections.fields.dayOrdinal", [("day", day)]));
    }

    [Theory]
    [InlineData(1234, "1234 archivos")]
    [InlineData(-1234, "-1234 archivos")]
    [InlineData(1234.5, "1234,5 archivos")]
    [InlineData(12345, "12.345 archivos")]
    public void Spanish_GroupsOnlyFromFiveDigits(double count, string expected)
    {
        // CLDR's minimumGroupingDigits 2 for es, which Intl.NumberFormat follows and .NET does not.
        var own = TextFixtures.Catalog("es", null, """{ "granary.test.files": "{count, plural, one {# archivo} other {# archivos}}" }""");
        var spanish = new TextLanguage(own, TextFixtures.EnglishCatalog(), null);

        Assert.Equal(expected, spanish.Get("granary.test.files", [("count", count)]));
    }

    [Fact]
    public void ABoolean_PrintsAsJavaScriptWritesIt()
    {
        Assert.Equal("Because you enjoy true", TextFixtures.Language("en").Get("home.section.becauseYouEnjoy", [("genres", true)]));
    }

    [Theory]
    [InlineData("en", "1.2345", "1.235 seconds")]
    [InlineData("en", "-1.2345", "-1.235 seconds")]
    [InlineData("en", "1.0005", "1.001 seconds")]
    [InlineData("en", "1234.5675", "1,234.568 seconds")]
    [InlineData("en", "1.2344999", "1.234 seconds")]
    [InlineData("de", "2.5005", "2,501 Sekunden")]
    public void AGroupedNumber_RoundsAHalfAwayFromZero(string tag, string seconds, string expected)
    {
        // Intl.NumberFormat's default (roundingMode halfExpand), to three decimals: 1.2345 is 1.235, never 1.234.
        decimal exact = decimal.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture);
        var language = TextFixtures.Language(tag);

        Assert.Equal(expected, language.Get("player.seekInterval.choice", [("seconds", exact)]));
        Assert.Equal(expected, language.Get("player.seekInterval.choice", [("seconds", (double)exact)]));
    }

    [Fact]
    public void ANumberElement_IsGroupedInTheLanguagesCulture()
    {
        // Upstream's one {day, number} (ar.json's web.collections.fields.dayOrdinal). Arabic numbers keep Latin digits
        // and separators for now: .NET would pair Arabic separators with Latin digits.
        Assert.Equal("1,234", TextFixtures.Language("ar").Get("web.collections.fields.dayOrdinal", [("day", 1234)]));
    }

    [Fact]
    public void ATag_ShowsItsChildrenAsPlainText()
    {
        var german = TextFixtures.Language("de");

        Assert.Equal("Gelesen von Elena Moreau", german.Get("web.audiobooks.player.narratedBy", [("narrator", "Elena Moreau")]));
    }

    [Fact]
    public void Runs_KeepTheTagsAroundTheirText()
    {
        var english = TextFixtures.Language("en");

        var runs = english.Runs("web.audiobooks.player.narratedBy", [("narrator", "Elena Moreau")]);

        Assert.Collection(runs,
            run => Assert.Equal(("Narrated by ", ""), (run.Text, string.Join('/', run.Tags))),
            run => Assert.Equal(("Elena Moreau", "name"), (run.Text, string.Join('/', run.Tags))));
    }

    [Fact]
    public void Runs_KeepAnEmptyTagsPlace_ForTheControlThatGoesThere()
    {
        var english = TextFixtures.Language("en");

        var runs = english.Runs("web.home.addRow.rules.groupSentence", []);

        Assert.Equal(["join", "", "w", "", "match", "", "w"], runs.Select(run => string.Join('/', run.Tags)));
        Assert.Equal(["", " ", "titles that match", " ", "", " ", "of these:"], runs.Select(run => run.Text));
        Assert.Equal(" titles that match  of these:", english.Get("web.home.addRow.rules.groupSentence", []));
    }

    [Fact]
    public void Runs_OfATagFirst_StartWithIt()
    {
        var french = TextFixtures.Language("fr");

        var runs = french.Runs("web.collections.starterPacks.newCount", [("count", 3)]);

        Assert.Collection(runs,
            run => Assert.Equal(("Nouvelles : ", ""), (run.Text, string.Join('/', run.Tags))),
            run => Assert.Equal(("3", "b"), (run.Text, string.Join('/', run.Tags))));
    }

    [Fact]
    public void AMessageTheLanguageLacks_ComesInEnglish()
    {
        var german = TextFixtures.Language("de");

        Assert.Equal("Save", german.Get("common.save"));
        Assert.Equal("Only in English", german.Get("granary.test.only"));
    }

    [Fact]
    public void AnEnglishFallback_KeepsEnglishPluralRulesAndNumbers()
    {
        // Japanese has no inCollections here: the English message formats as English would, never with Japanese rules,
        // which know only "other".
        var japanese = TextFixtures.Language("ja");

        Assert.Equal("In 1,000 collections", japanese.Get("web.collections.copy.inCollections", [("count", 1000), ("count_text", "1,000")]));
        Assert.Equal("In one collection", japanese.Get("web.collections.copy.inCollections", [("count", 1), ("count_text", "one")]));
    }

    [Fact]
    public void GranarysOwnTexts_FormatLikeTheWebs()
    {
        Assert.Equal("1 file", TextFixtures.Language("en").Get("granary.test.files", [("count", 1)]));
        Assert.Equal("1.000 Dateien", TextFixtures.Language("de").Get("granary.test.files", [("count", 1000)]));
    }

    // What is missing shows marked in a debug build and as nothing in a release, as the web shows nothing. Both are
    // tested in either build: CI runs the tests from a release build.
    [Theory]
    [InlineData(true, "[web.nowhere.toBeFound]")]
    [InlineData(false, "")]
    public void AKeyMissingInEnglish_ShowsTheKeyInBracketsWhereProblemsAreMarked_AndIsLoggedOnce(bool marksProblems, string expected)
    {
        var logs = new LogSink();
        var german = TextFixtures.Language("de", logs.For<TextLanguage>(), marksProblems);

        string first = german.Get("web.nowhere.toBeFound");
        string second = german.Get("web.nowhere.toBeFound");

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
        Assert.Equal(expected, string.Concat(german.Runs("web.nowhere.toBeFound").Select(run => run.Text)));
        Assert.Single(logs.Lines, line => line.Contains("web.nowhere.toBeFound", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "Because you enjoy {genres}")]
    [InlineData(false, "Because you enjoy ")]
    public void AMissingValue_ShowsItsPlaceholderWhereProblemsAreMarked_AndIsLoggedOnce(bool marksProblems, string expected)
    {
        var logs = new LogSink();
        var english = TextFixtures.Language("en", logs.For<TextLanguage>(), marksProblems);

        string text = english.Get("home.section.becauseYouEnjoy");
        english.Get("home.section.becauseYouEnjoy");

        Assert.Equal(expected, text);
        Assert.Single(logs.Lines, line => line.Contains("genres", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingValueOfAFallbackMessage_IsMarkedTheSameWay()
    {
        var japanese = TextFixtures.Language("ja", marksProblems: true);

        Assert.Equal("Because you enjoy {genres}", japanese.Get("home.section.becauseYouEnjoy"));
    }

    [Fact]
    public void OnlyADebugBuild_MarksProblemsByItself()
    {
#if DEBUG
        const string expected = "[web.nowhere.toBeFound]";
#else
        const string expected = "";
#endif

        Assert.Equal(expected, TextFixtures.Language("en").Get("web.nowhere.toBeFound"));
    }

    [Fact]
    public void ANullValue_ShowsNothing()
    {
        Assert.Equal("Because you enjoy ", TextFixtures.Language("en").Get("home.section.becauseYouEnjoy", [("genres", null)]));
    }

    [Fact]
    public void APluralOfSomethingElse_TakesOther_AndIsLogged()
    {
        var logs = new LogSink();
        var english = TextFixtures.Language("en", logs.For<TextLanguage>());

        string text = english.Get("player.seekInterval.choice", [("seconds", "many")]);

        Assert.Equal(" seconds", text);
        Assert.Single(logs.Lines);
    }

    [Fact]
    public void InArabic_PrintedStrings_AreIsolated_AndBareNumbersAreNot()
    {
        var arabic = TextFixtures.Language("ar");

        Assert.Equal($"لأنك تحب {Fsi}Drama{Pdi}", arabic.Get("home.section.becauseYouEnjoy", [("genres", "Drama")]));
        Assert.Equal("لأنك تحب 42", arabic.Get("home.section.becauseYouEnjoy", [("genres", 42)]));
        Assert.Equal($"في {Fsi}3-4{Pdi} مجموعات", arabic.Get("web.collections.copy.inCollections", [("count", 3), ("count_text", "3-4")]));
    }

    [Theory]
    [InlineData(-5, "-5")]
    [InlineData(1.5, "1.5")]
    public void InArabic_ANumberWithASignOrADecimalMark_IsIsolated_AsOnTheWeb(double number, string printed)
    {
        // The web prints a number value first and isolates what is not digits alone (format.ts, prepareValues: "A range
        // or a signed number keeps its isolate, or Arabic text would reorder it"), so the printed text decides, not the type.
        var arabic = TextFixtures.Language("ar");

        Assert.Equal($"لأنك تحب {Fsi}{printed}{Pdi}", arabic.Get("home.section.becauseYouEnjoy", [("genres", number)]));
    }

    [Fact]
    public void InArabic_AnEnglishFallback_IsIsolatedToo()
    {
        var arabic = TextFixtures.Language("ar");

        Assert.Equal($"Narrated by {Fsi}Elena{Pdi}", arabic.Get("web.audiobooks.player.narratedBy", [("narrator", "Elena")]));
    }

    [Fact]
    public void InArabic_AValueASelectChoosesBy_IsNotIsolated()
    {
        var own = TextFixtures.Catalog("ar", null, """{ "granary.test.kind": "{kind, select, movie {Movies} other {{kind}}}" }""");
        var arabic = new TextLanguage(own, TextFixtures.EnglishCatalog(), null);

        Assert.Equal("Anime", arabic.Get("granary.test.kind", [("kind", "Anime")]));
    }

    [Fact]
    public void English_KeepsItsBytes()
    {
        var english = TextFixtures.Language("en");

        Assert.Equal("Because you enjoy Drama", english.Get("home.section.becauseYouEnjoy", [("genres", "Drama")]));
    }

    private static TextLanguage TextLanguageWith(string key, string icu)
    {
        var catalog = TextFixtures.Catalog("en", null, $$"""{ "{{key}}": "{{icu}}" }""");
        return new TextLanguage(catalog, catalog, null);
    }
}
