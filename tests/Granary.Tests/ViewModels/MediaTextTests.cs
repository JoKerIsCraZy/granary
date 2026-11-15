using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>
/// Sizes, rates and speeds as the web writes them (lib/mediaFormat.ts, player/utils/playbackSpeed.ts, the admin
/// dashboard's format.ts): the number in the format locale, the unit in the web's words for the language.
/// </summary>
public sealed class MediaTextTests
{
    private const long GigaAndAHalf = 1_610_612_736;
    private const char Fsi = '⁨';
    private const char Pdi = '⁩';

    [Theory]
    [InlineData("en", "1.5 GB", "1.5 GiB", "12,345 kbps", "12.3 Mbps", "48,000 Hz", "1.25×")]
    [InlineData("de", "1,5 GB", "1,5 GiB", "12.345 kbps", "12,3 Mbps", "48.000 Hz", "1,25×")]
    [InlineData("fr", "1,5 Go", "1,5 Gio", "12 345 kbit/s", "12,3 Mbit/s", "48 000 Hz", "1,25×")]
    [InlineData("ja", "1.5 GB", "1.5 GiB", "12,345 kbps", "12.3 Mbps", "48,000 Hz", "1.25×")]
    public void SizesAndRates_AreInTheLanguage(string tag, string size, string binarySize, string kbps, string mbps, string hertz, string speed)
    {
        using var _ = AppText.Use(TextLanguage.Load(tag));

        Assert.Equal(size, MediaText.FileSize(GigaAndAHalf));
        Assert.Equal(binarySize, MediaText.FileSize(GigaAndAHalf, iecUnits: true));
        Assert.Equal(kbps, MediaText.Bitrate(12_345));
        Assert.Equal(mbps, MediaText.MbpsFromKbps(12_345));
        Assert.Equal(hertz, MediaText.SampleRate(48_000));
        Assert.Equal(speed, PlaybackSpeeds.Format(1.25));
    }

    [Fact]
    public void InArabic_TheNumbers_AreArabicIndic_AndIsolatedBesideTheirUnit()
    {
        using var _ = AppText.Use(TextLanguage.Load("ar"));

        Assert.Equal($"{Fsi}١٫٥{Pdi} GB", MediaText.FileSize(GigaAndAHalf));
        Assert.Equal("٢×", PlaybackSpeeds.Format(2));
    }

    [Fact]
    public void NoSize_IsTheFallback()
    {
        Assert.Equal("", MediaText.FileSize(0));
        Assert.Equal("—", MediaText.MbpsFromKbps(0));
        Assert.Equal("500 B", MediaText.FileSize(500));
    }

    [Theory]
    [InlineData("en", "0.7 Mbps", "48.3 GB", "3.1h", "0.06 ms", "12,480")]
    [InlineData("de", "0,7 Mbps", "48,3 GB", "3,1h", "0,06 ms", "12.480")]
    [InlineData("de-CH", "0.7 Mbps", "48.3 GB", "3.1h", "0.06 ms", "12’480")]
    public void TheDashboardsNumbers_AreTheFormatLocales(string formats, string mbps, string data, string watched, string latency, string count)
    {
        using var _ = AppText.Use(TextLanguage.Load("en", formats: FormatLocale.For(formats)));

        Assert.Equal(mbps, AdminText.Mbps(0.7));
        Assert.Equal(data, AdminText.DataTotal(48_300_000_000));
        Assert.Equal(watched, AdminText.WatchTime(11_160));
        Assert.Equal(latency, AdminText.Latency(0.06));
        Assert.Equal(count, AdminText.Count(12_480));
    }
}
