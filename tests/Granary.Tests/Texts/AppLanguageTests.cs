using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// Which language Granary shows, by Silo's rules (docs/architecture/i18n.md, "Choosing the language at runtime"; the web's
/// <c>lib/appLocale.ts</c>): the profile's <c>ui.app_language</c>, then Windows' preferred languages, then English, each
/// matched only against the languages that are enabled. The expected matches are what the web's
/// <c>matchShippedLanguage</c> gives.
/// </summary>
public sealed class AppLanguageTests
{
    private static readonly string[] AllEnabled = ["en", "es", "de", "fr", "pt-BR", "ja", "zh-Hans", "ar"];

    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("EN-us", "en")]
    [InlineData("en_US", "en")]
    [InlineData("es-MX", "es")]
    [InlineData("es-419", "es")]
    [InlineData("de-AT", "de")]
    [InlineData("fr-CA", "fr")]
    [InlineData("pt", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt_br", "pt-BR")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ar-EG", "ar")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-hans-hk", "zh-Hans")]
    [InlineData("en-US-u-nu-arab", "en")]
    public void ATag_MatchesTheShippedLanguageOfItsLanguageAndScript(string tag, string expected)
    {
        Assert.Equal(expected, AppLanguage.Match(tag));
    }

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("zh-HK")]
    [InlineData("zh-MO")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hant-CN")]
    [InlineData("zh-US")]
    [InlineData("ja-Latn")]
    [InlineData("ar-Latn")]
    [InlineData("it")]
    [InlineData("ko-KR")]
    [InlineData("zh-CHS")]
    [InlineData("x-foo")]
    [InlineData("")]
    [InlineData(null)]
    public void ATag_OfAnotherLanguageOrScript_MatchesNone(string? tag)
    {
        Assert.Null(AppLanguage.Match(tag));
    }

    [Fact]
    public void TheProfilesLanguage_ComesFirst()
    {
        Assert.Equal("fr", AppLanguage.Resolve("fr", ["de-DE", "en-US"], AllEnabled));
    }

    [Fact]
    public void AStoredLanguageName_IsMatchedToo()
    {
        // As the web's canonicalLanguageTag reads a stored value (lib/languageTags.ts).
        Assert.Equal("pt-BR", AppLanguage.Resolve("Brazilian Portuguese", [], AllEnabled));
        Assert.Equal("de", AppLanguage.Resolve(" german ", [], AllEnabled));
    }

    [Fact]
    public void WithoutOne_TheFirstWindowsLanguageThatMatches_Wins()
    {
        Assert.Equal("de", AppLanguage.Resolve(null, ["ko-KR", "de-DE", "fr-FR"], AllEnabled));
    }

    [Fact]
    public void AnUnmatchedProfileLanguage_FollowsWindows()
    {
        Assert.Equal("ja", AppLanguage.Resolve("zh-TW", ["ja-JP"], AllEnabled));
    }

    [Fact]
    public void ALanguageNotEnabled_IsPassedOver_WhereverItComesFrom()
    {
        string[] enabled = ["en", "de"];

        Assert.Equal("de", AppLanguage.Resolve("fr", ["es-ES", "de-CH"], enabled));
    }

    [Fact]
    public void EnglishInWindowsList_StopsTheSearch()
    {
        // The web takes English from the browser's list even before a later language with a catalog.
        Assert.Equal("en", AppLanguage.Resolve(null, ["en-GB", "de-DE"], AllEnabled));
    }

    [Fact]
    public void NothingThatMatches_IsEnglish()
    {
        Assert.Equal("en", AppLanguage.Resolve(null, ["ko-KR", "zh-TW"], AllEnabled));
        Assert.Equal("en", AppLanguage.Resolve(null, [], []));
    }

    [Fact]
    public void ForNow_OnlyEnglishIsEnabled_SoEveryoneGetsEnglish()
    {
        Assert.Equal(["en"], AppLanguage.Enabled);
        Assert.Equal("en", AppLanguage.Resolve("de", ["de-DE", "fr-FR"], AppLanguage.Enabled));
    }

    [Fact]
    public void TheShippedLanguages_AreTheWebs()
    {
        Assert.Equal(AllEnabled, AppLanguage.Shipped);
    }

    [Theory]
    [InlineData("ar", true)]
    [InlineData("ar-SA", true)]
    [InlineData("en", false)]
    [InlineData("he", false)]
    public void OnlyArabic_IsWrittenRightToLeft(string tag, bool expected)
    {
        // Among the shipped languages; the web's interfaceDirection.
        Assert.Equal(expected, AppLanguage.IsRightToLeft(tag));
    }
}
