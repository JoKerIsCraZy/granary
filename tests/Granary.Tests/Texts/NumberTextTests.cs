using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// Numbers outside messages, as the web writes them with the format locale (lib/numberFormat.ts' formatDecimal,
/// <c>toLocaleString(formatLocale())</c>) and with the messages' digits (i18n/format.ts' numberText). The expected
/// texts are what Intl.NumberFormat gives for the same locale.
/// </summary>
public sealed class NumberTextTests
{
    [Theory]
    [InlineData("en", 1.5, 1, 1, "1.5")]
    [InlineData("en", 1234.5, 1, 1, "1234.5")]
    [InlineData("en", 2, 1, 1, "2.0")]
    [InlineData("en", 2, 1, 0, "2")]
    [InlineData("en", 2.25, 1, 0, "2.3")]
    [InlineData("de", 1.5, 1, 1, "1,5")]
    [InlineData("de", 1234.5, 1, 1, "1234,5")]
    [InlineData("fr", 12.3, 1, 1, "12,3")]
    [InlineData("es", 12345.6, 1, 1, "12345,6")]
    [InlineData("ja", 1.5, 1, 1, "1.5")]
    [InlineData("ar", 1.5, 1, 1, "١٫٥")]
    [InlineData("ar-AE", 1.5, 1, 1, "1.5")]
    public void ADecimal_IsWrittenInTheFormatLocale_WithoutGrouping(string formats, double value, int max, int min, string expected)
    {
        using var _ = AppText.Use(TextLanguage.Load("en", formats: FormatLocale.For(formats)));

        Assert.Equal(expected, NumberText.Decimal(value, max, min));
    }

    [Theory]
    [InlineData(1.005, 2, "1.00")]
    [InlineData(0.25, 1, "0.3")]
    [InlineData(12.35, 1, "12.3")]
    [InlineData(2.675, 2, "2.67")]
    [InlineData(0.04, 1, "0.0")]
    public void ADecimal_RoundsTheExactValue_AsToFixedDoes(double value, int digits, string expected)
    {
        // The web rounds with toFixed, which rounds the binary value itself (1.005 is a little less, so 1.00) and a
        // true half up (0.25 → 0.3), where .NET's "F1" would round that half to even.
        Assert.Equal(expected, NumberText.Decimal(value, digits));
    }

    [Theory]
    [InlineData("en", 1234567, "1,234,567")]
    [InlineData("en", 1234.5678, "1,234.568")]
    [InlineData("de", 1234567, "1.234.567")]
    [InlineData("de-CH", 1234567, "1’234’567")]
    [InlineData("fr", 1234567, "1 234 567")]
    [InlineData("es", 1234, "1234")]
    [InlineData("es", 12345, "12.345")]
    [InlineData("es-MX", 1234, "1,234")]
    [InlineData("pl", 1234, "1234")]
    [InlineData("ja", 1234567, "1,234,567")]
    [InlineData("ar", 1234567, "١٬٢٣٤٬٥٦٧")]
    [InlineData("ar-AE", 1234567, "1,234,567")]
    public void AGroupedNumber_IsWrittenAsTheFormatLocaleWritesIt(string formats, double value, string expected)
    {
        using var _ = AppText.Use(TextLanguage.Load("en", formats: FormatLocale.For(formats)));

        Assert.Equal(expected, NumberText.Grouped(value));
    }

    [Theory]
    [InlineData("en", "en", "1:05:09")]
    [InlineData("ar", "ar", "١:٠٥:٠٩")]
    [InlineData("ar", "ar-EG", "١:٠٥:٠٩")]
    [InlineData("ar", "ar-AE", "1:05:09")]
    [InlineData("en", "ar-EG", "1:05:09")]
    [InlineData("de", "de", "1:05:09")]
    public void Digits_AreTheMessagesDigits(string language, string formats, string expected)
    {
        // Only Arabic takes its format locale's digits into its messages; English keeps its bytes even on an Arabic
        // region's format (the web's numberText and its tests).
        using var _ = AppText.Use(TextLanguage.Load(language, formats: FormatLocale.For(formats)));

        Assert.Equal(expected, NumberText.Digits("1:05:09"));
    }

    [Fact]
    public void ANumberBeyondWhatADecimalHolds_StillShows()
    {
        // A garbled server value must not take its page down.
        Assert.Equal("1E+30", NumberText.Grouped(1e30));
        Assert.Equal("1E+30", NumberText.Decimal(1e30, 1));
        Assert.Equal("NaN", NumberText.Grouped(double.NaN, 0, 1));
    }

    [Fact]
    public void ACountText_IsGroupedInTheFormatLocale()
    {
        // The web passes a count's text as count.toLocaleString(formatLocale()).
        using (AppText.Use(TextLanguage.Load("de", formats: FormatLocale.For("de-CH"))))
        {
            Assert.Equal("1’234", AppText.Number(1234));
        }
        using (AppText.Use(TextLanguage.Load("ar")))
        {
            Assert.Equal("١٬٢٣٤", AppText.Number(1234));
        }
    }
}
