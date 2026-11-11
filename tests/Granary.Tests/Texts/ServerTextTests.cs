using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// Text a Silo server sends in English with the key of its catalog message, such as a row's title: in the language on
/// screen when that language's catalog has the key and the server's values fill the message, otherwise as the server
/// sent it, and in English always as sent (the web's <c>serverText</c>, web/src/i18n/format.ts).
/// </summary>
public sealed class ServerTextTests
{
    private const string EnjoyKey = "home.section.becauseYouEnjoy";

    [Fact]
    public void ALanguageWithTheKey_ShowsItsMessage_FilledFromTheServersValues()
    {
        var german = TextFixtures.Language("de");

        Assert.Equal("Weil du Krimis magst", german.ServerText(EnjoyKey, Values(("genres", "Krimis")), "Because you enjoy Crime"));
    }

    [Fact]
    public void English_ShowsTheServersText_AsSent()
    {
        var english = TextFixtures.Language("en");

        Assert.Equal("Because You Enjoy Crime", english.ServerText(EnjoyKey, Values(("genres", "Crime")), "Because You Enjoy Crime"));
    }

    [Fact]
    public void ALanguageWithoutTheKey_ShowsTheServersText_NotTheEnglishCatalogs()
    {
        // The German fixture leaves out common.save, which English has as "Save".
        var german = TextFixtures.Language("de");

        Assert.Equal("Save it", german.ServerText("common.save", null, "Save it"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("home.section.inNoCatalog")]
    public void NoKeyOrAnUnknownOne_ShowsTheServersText(string? key)
    {
        var german = TextFixtures.Language("de");

        Assert.Equal("Picked by Elena", german.ServerText(key, Values(("genres", "Krimis")), "Picked by Elena"));
    }

    [Fact]
    public void AValueTheMessageNeeds_Missing_ShowsTheServersText()
    {
        var german = TextFixtures.Language("de");

        Assert.Equal("Because you enjoy Crime", german.ServerText(EnjoyKey, Values(("genre", "Krimis")), "Because you enjoy Crime"));
        Assert.Equal("Because you enjoy Crime", german.ServerText(EnjoyKey, null, "Because you enjoy Crime"));
        Assert.Equal("Because you enjoy Crime", german.ServerText(EnjoyKey, new Dictionary<string, string> { ["genres"] = null! },
            "Because you enjoy Crime"));
    }

    [Theory]
    [InlineData("movie", "Filme")]
    [InlineData("series", "Serien")]
    public void AValueASelectChoosesBy_PicksItsBranch(string kind, string text)
    {
        var german = TextFixtures.Language("de");

        Assert.Equal(text, german.ServerText("web.collections.templateGallery.listKind", Values(("kind", kind)), "Shows"));
    }

    [Fact]
    public void AValueASelectChoosesBy_Missing_ShowsTheServersText()
    {
        var german = TextFixtures.Language("de");

        Assert.Equal("Shows", german.ServerText("web.collections.templateGallery.listKind", Values(), "Shows"));
    }

    [Fact]
    public void InArabic_TheServersValues_AreIsolated()
    {
        var arabic = TextFixtures.Language("ar");

        Assert.Equal("لأنك تحب ⁨Crime⁩", arabic.ServerText(EnjoyKey, Values(("genres", "Crime")), "Because you enjoy Crime"));
    }

    [Fact]
    public void AppText_UsesTheLanguageOnScreen()
    {
        using var _ = AppText.Use(TextFixtures.Language("de"));

        Assert.Equal("Weil du Krimis magst", AppText.ServerText(EnjoyKey, Values(("genres", "Krimis")), "Because you enjoy Crime"));
    }

    private static Dictionary<string, string> Values(params (string Name, string Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
}
