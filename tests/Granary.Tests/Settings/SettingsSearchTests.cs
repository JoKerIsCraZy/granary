using Granary.Core.Settings.Search;

namespace Granary.Tests.Settings;

/// <summary>
/// The settings' search matches as the web's does (web/src/components/settings/settingsSearch.ts and its test): every word
/// of the query starts a word of a page's text, or sits inside one from four letters on; a group's own name brings all its
/// pages. Letters of any script count, accents do not.
/// </summary>
public sealed class SettingsSearchTests
{
    // The web test's groups.
    private static readonly SettingsSearchGroup[] Groups =
    [
        new("server", "Server", [],
        [
            Page("general", "General", "Authentication and logging", ["access token", "refresh token", "log level"], Row("Quiet Subsystems")),
            Page("database", "Database", "Postgres and Redis", ["connection url", "pool"], Row("Pool Max Open")),
        ]),
        new("playback", "Playback", [],
        [
            Page("subtitles", "Subtitles", "Skipping behavior, language, and style", ["forced subtitles", "captions"]),
        ]),
    ];

    [Theory]
    [InlineData("redis", "database")]
    [InlineData("access token", "general")]
    [InlineData("forced", "subtitles")]
    public void APage_MatchesByItsLabelDescriptionOrKeywords(string query, string page)
    {
        Assert.Equal([page], Tags(SettingsSearch.FilterGroups(Groups, query)));
    }

    [Fact]
    public void AGroupsName_BringsAllItsPages()
    {
        var found = SettingsSearch.FilterGroups(Groups, "server");

        Assert.Equal(["general", "database"], Tags(found));
        Assert.Equal(2, SettingsSearch.CountPages(found));
    }

    [Fact]
    public void AGroupsKeywords_BringAllItsPages()
    {
        // A translated heading keeps its English name as a keyword.
        SettingsSearchGroup[] translated = [new("playback", "Reproducción", ["Playback"], [Page("subtitles", "Subtítulos", "Comportamiento y estilo", [])])];

        Assert.Equal(["subtitles"], Tags(SettingsSearch.FilterGroups(translated, "playback")));
    }

    [Fact]
    public void AShortWord_DoesNotMatchInsideAWord()
    {
        Assert.Empty(SettingsSearch.FilterGroups(Groups, "pin"));
    }

    [Fact]
    public void AWordOfFourLetters_MatchesInsideAWord()
    {
        Assert.Equal(["general"], Tags(SettingsSearch.FilterGroups(Groups, "system")));
    }

    [Fact]
    public void ASetting_MatchesItsPage()
    {
        Assert.Equal(["database"], Tags(SettingsSearch.FilterGroups(Groups, "max open")));
    }

    [Fact]
    public void EveryWordOfTheQuery_MustMatch()
    {
        Assert.Empty(SettingsSearch.FilterGroups(Groups, "redis captions"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    public void AQueryWithoutWords_KeepsEveryPage(string query)
    {
        Assert.Equal(["general", "database", "subtitles"], Tags(SettingsSearch.FilterGroups(Groups, query)));
    }

    [Theory]
    [InlineData("untertitel")]
    [InlineData("UNTERTITEL")]
    [InlineData("hörbuch")]
    [InlineData("horbuch")]
    [InlineData("geräte")]
    public void Case_AndAccents_DoNotMatter(string query)
    {
        SettingsSearchGroup[] german = [new("playback", "Wiedergabe", [], [Page("audio", "Hörbücher & Untertitel", null, [], Row("Geräte"))])];

        Assert.Equal(["audio"], Tags(SettingsSearch.FilterGroups(german, query)));
    }

    [Theory]
    [InlineData("字幕")]
    [InlineData("字幕の")]
    [InlineData("сжатие")]
    public void LettersOfAnyScript_Match(string query)
    {
        SettingsSearchGroup[] pages =
        [
            new("playback", "再生", [], [Page("subtitles", "字幕の言語", null, [], Row("Сжатие видео"))]),
        ];

        Assert.Equal(["subtitles"], Tags(SettingsSearch.FilterGroups(pages, query)));
    }

    [Fact]
    public void AShortWordOfAnotherScript_DoesNotMatchInsideAWord()
    {
        SettingsSearchGroup[] pages = [new("playback", "再生", [], [Page("subtitles", "字幕の言語", null, [])])];

        Assert.Empty(SettingsSearch.FilterGroups(pages, "言語"));
    }

    /// <summary>
    /// A half of an emoji, as an input method leaves it while it composes, is no letter and breaks nothing. The half is made
    /// here, not given in the attribute, which keeps its strings in UTF-8 and so cannot carry a lone half.
    /// </summary>
    [Theory]
    [InlineData(0xD83D, false)]
    [InlineData(0xDE00, true)]
    public void ALoneHalfOfACharacter_IsNoLetter(int half, bool isFirst)
    {
        string query = isFirst ? $"{(char)half}redis" : $"redis{(char)half}";
        Assert.True(char.IsSurrogate(query, isFirst ? 0 : query.Length - 1));

        Assert.Equal(["database"], Tags(SettingsSearch.FilterGroups(Groups, query)));
    }

    [Fact]
    public void AQueryOfALoneHalfOfACharacter_KeepsEveryPage()
    {
        Assert.Equal(["general", "database", "subtitles"], Tags(SettingsSearch.FilterGroups(Groups, "\uD83D")));
    }

    [Fact]
    public void APageTextWithALoneHalfOfACharacter_StillMatches()
    {
        SettingsSearchGroup[] pages = [new("playback", "Playback", [], [Page("quality", "Qualität \uD83D", null, [])])];

        Assert.Equal(["quality"], Tags(SettingsSearch.FilterGroups(pages, "qualitat")));
    }

    [Fact]
    public void ThePagesSettings_MatchOneByOne()
    {
        SettingsSearchEntry[] settings = [Row("Preferred quality"), Row("Max bitrate", "Caps the stream"), Row("Software decoding", keywords: ["gpu"])];

        Assert.Equal(["Max bitrate"], Labels(SettingsSearch.FilterEntries(settings, "stream")));
        Assert.Equal(["Software decoding"], Labels(SettingsSearch.FilterEntries(settings, "gpu")));
        Assert.Equal(["Preferred quality"], Labels(SettingsSearch.FilterEntries(settings, "quali")));
        Assert.Empty(SettingsSearch.FilterEntries(settings, ""));
    }

    [Fact]
    public void WordsSpreadOverSettings_MatchThePage_ButNoSingleSetting()
    {
        var page = Groups[0].Pages[1];

        Assert.Equal(["database"], Tags(SettingsSearch.FilterGroups(Groups, "postgres pool")));
        Assert.Empty(SettingsSearch.FilterEntries(page.Settings, "postgres pool"));
    }

    private static SettingsSearchPage Page(string tag, string label, string? description, string[] keywords, params SettingsSearchEntry[] settings) =>
        new(tag, label, description, keywords, settings);

    private static SettingsSearchEntry Row(string label, string? description = null, string[]? keywords = null) =>
        new(SettingsEntryKind.Row, label, description, keywords ?? []);

    private static string[] Tags(IEnumerable<SettingsSearchGroup> groups) => [.. groups.SelectMany(group => group.Pages).Select(page => page.Tag)];

    private static string[] Labels(IEnumerable<SettingsSearchEntry> entries) => [.. entries.Select(entry => entry.Label)];
}
