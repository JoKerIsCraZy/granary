using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// A language's catalog: the web's compiled messages (Catalogs/web/&lt;tag&gt;.json) and Granary's own ICU texts
/// (Catalogs/granary/&lt;tag&gt;.json), embedded in Granary.Core. A message that cannot be read is left out and said in
/// <see cref="TextCatalog.Problems"/>, never thrown: the rest of the catalog still shows.
/// </summary>
public sealed class TextCatalogTests
{
    [Fact]
    public void BothParts_AreRead()
    {
        var catalog = TextFixtures.EnglishCatalog();

        Assert.True(catalog.Contains("common.cancel"));
        Assert.True(catalog.Contains("granary.test.files"));
        Assert.False(catalog.Contains("@granary.test.files"));
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void NoCatalogAtAll_IsAnEmptyCatalog()
    {
        var catalog = TextFixtures.Catalog("de", null, null);

        Assert.Equal(0, catalog.Count);
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void AMessageThatCannotBeRead_IsLeftOut_AndTheRestStays()
    {
        var catalog = TextFixtures.Catalog("en",
            """
            {
              "common.cancel": [{"type": 0, "value": "Cancel"}],
              "web.broken.type": [{"type": 99, "value": "x"}],
              "web.broken.ordinal": [{"type": 6, "value": "n", "options": {"other": {"value": []}}, "offset": 0, "pluralType": "ordinal"}],
              "web.broken.shape": {"type": 0},
              "web.broken.style": [{"type": 2, "value": "share", "style": "percent"}]
            }
            """,
            """
            {
              "granary.fine": "Fine",
              "granary.broken": "{name, plural, one {x}}",
              "common.cancel": "Not Granary's to say",
              "granary.notText": 5
            }
            """);

        Assert.True(catalog.Contains("common.cancel"));
        Assert.True(catalog.Contains("granary.fine"));
        Assert.Equal(2, catalog.Count);
        Assert.Equal(7, catalog.Problems.Count);
        Assert.Contains(catalog.Problems, problem => problem.Contains("web.broken.style", StringComparison.Ordinal));
        Assert.Contains(catalog.Problems, problem => problem.Contains("web.broken.ordinal", StringComparison.Ordinal));
        Assert.Contains(catalog.Problems, problem => problem.Contains("granary.broken", StringComparison.Ordinal));
    }

    [Fact]
    public void GranarysTexts_NeverReplaceTheWebs()
    {
        var catalog = TextFixtures.Catalog("en",
            """{ "common.cancel": [{"type": 0, "value": "Cancel"}] }""",
            """{ "common.cancel": "Abort" }""");

        Assert.Equal("Cancel", new TextLanguage(catalog, catalog, null).Get("common.cancel"));
    }

    [Fact]
    public void AFileThatIsNoJson_IsAProblem_NotAnError()
    {
        var catalog = TextFixtures.Catalog("en", "not json", """{ "granary.fine": "Fine" }""");

        Assert.True(catalog.Contains("granary.fine"));
        Assert.Single(catalog.Problems);
    }

    [Fact]
    public void EveryShippedCatalog_ReadsWithoutProblems()
    {
        // The catalogs Granary embeds: on a branch without the web's, only Granary's own.
        foreach (string tag in AppLanguage.Shipped)
        {
            var catalog = TextCatalog.Embedded(tag);

            Assert.Equal(tag, catalog.Tag);
            Assert.Empty(catalog.Problems);
        }
    }

    [Fact]
    public void GranarysTranslations_TranslateOnlyItsEnglishKeys()
    {
        var english = TextCatalog.Embedded(AppLanguage.English);

        foreach (string tag in AppLanguage.Shipped)
        {
            var strays = TextCatalog.Embedded(tag).GranaryKeys.Where(key => !english.GranaryKeys.Contains(key)).ToList();

            Assert.Empty(strays);
        }
    }

    [Fact]
    public void AnEmbeddedCatalog_IsReadOnce()
    {
        Assert.Same(TextCatalog.Embedded("de"), TextCatalog.Embedded("de"));
    }
}
