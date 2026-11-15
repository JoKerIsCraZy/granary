using Granary.App.Controls;
using Granary.Core.Texts;

namespace Granary.Tests.ViewModels;

/// <summary>The player's times, as the web's player/utils/formatTime.ts writes them and its tests expect.</summary>
public sealed class PlayerTimeTests
{
    [Fact]
    public void Times_AreMinutesAndSeconds_ThenHours()
    {
        Assert.Equal(["0:00", "1:05", "1:30:41"], new[] { 0.0, 65, 5441 }.Select(seconds => PlayerTime.Format(seconds)));
        Assert.Equal("0:04:05", PlayerTime.Format(245, duration: 3600));
        Assert.Equal("-16:46", PlayerTime.Remaining(1915, 2921));
    }

    [Theory]
    [InlineData("de", "de", "1:30:41")]
    [InlineData("ar", "ar-EG", "١:٣٠:٤١")]
    [InlineData("ar", "ar-AE", "1:30:41")]
    [InlineData("en", "ar-EG", "1:30:41")]
    public void Times_AreInTheScreensDigits(string language, string formats, string expected)
    {
        using var _ = AppText.Use(TextLanguage.Load(language, formats: FormatLocale.For(formats)));

        Assert.Equal(expected, PlayerTime.Format(5441));
    }
}
