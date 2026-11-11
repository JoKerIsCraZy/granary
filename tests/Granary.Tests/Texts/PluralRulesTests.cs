using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// CLDR's cardinal plural rules for the languages Silo's web ships, the categories its catalogs carry
/// (docs/architecture/i18n.md, "Messages": es, fr and pt-BR need many, ar needs all six). The expected categories are
/// what <c>Intl.PluralRules</c> gives (CLDR 48).
/// </summary>
public sealed class PluralRulesTests
{
    [Theory]
    [InlineData("en", "0", "other")]
    [InlineData("en", "1", "one")]
    [InlineData("en", "2", "other")]
    [InlineData("en", "1.5", "other")]
    [InlineData("en", "1000000", "other")]
    [InlineData("de", "1", "one")]
    [InlineData("de", "0", "other")]
    [InlineData("de", "21", "other")]
    [InlineData("es", "1", "one")]
    [InlineData("es", "0", "other")]
    [InlineData("es", "1.5", "other")]
    [InlineData("es", "1000000", "many")]
    [InlineData("es", "2000000", "many")]
    [InlineData("es", "1000001", "other")]
    [InlineData("es", "1000000.5", "other")]
    [InlineData("fr", "0", "one")]
    [InlineData("fr", "1", "one")]
    [InlineData("fr", "1.5", "one")]
    [InlineData("fr", "2", "other")]
    [InlineData("fr", "1000000", "many")]
    [InlineData("pt-BR", "0", "one")]
    [InlineData("pt-BR", "1", "one")]
    [InlineData("pt-BR", "1.5", "one")]
    [InlineData("pt-BR", "2", "other")]
    [InlineData("pt-BR", "3000000", "many")]
    [InlineData("ja", "1", "other")]
    [InlineData("ja", "0", "other")]
    [InlineData("zh-Hans", "1", "other")]
    [InlineData("ar", "0", "zero")]
    [InlineData("ar", "1", "one")]
    [InlineData("ar", "2", "two")]
    [InlineData("ar", "3", "few")]
    [InlineData("ar", "10", "few")]
    [InlineData("ar", "103", "few")]
    [InlineData("ar", "11", "many")]
    [InlineData("ar", "99", "many")]
    [InlineData("ar", "111", "many")]
    [InlineData("ar", "100", "other")]
    [InlineData("ar", "102", "other")]
    [InlineData("ar", "1.5", "other")]
    [InlineData("ar", "3.5", "other")]
    public void ACount_TakesItsLanguagesCategory(string tag, string count, string expected)
    {
        Assert.Equal(expected, PluralRules.Category(tag, decimal.Parse(count, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("1.0", "one")]
    [InlineData("1.0004", "one")]
    [InlineData("1.0005", "other")]
    [InlineData("-1", "one")]
    public void ACount_IsRoundedToThreeDecimals_AndItsSignIgnored(string count, string expected)
    {
        // Intl.PluralRules rounds to three fraction digits before choosing, as the number shows.
        Assert.Equal(expected, PluralRules.Category("en", decimal.Parse(count, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ALanguageWithoutRules_HasOnlyOther()
    {
        Assert.Equal("other", PluralRules.Category("ko", 1));
    }
}
