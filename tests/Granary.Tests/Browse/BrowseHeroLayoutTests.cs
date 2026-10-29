using System.Globalization;
using System.Text.RegularExpressions;
using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

public sealed partial class BrowseHeroLayoutTests
{
    [GeneratedRegex(@"\d[.,]\d")]
    private static partial Regex DecimalNumber();

    [Fact]
    public void TheFoldsExpressions_CarryNoNumberOfTheirOwn_UnderAnyCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = BrowseHeroLayout.Expressions.Select(e => e.Text).ToList();
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = BrowseHeroLayout.Expressions.Select(e => e.Text).ToList();

            Assert.Equal(invariant, german);
            Assert.All(german, text => Assert.DoesNotMatch(DecimalNumber(), text));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [GeneratedRegex(@"\bp\.([A-Za-z]+)")]
    private static partial Regex FoldNumber();

    [Fact]
    public void TheExpressions_ReadEveryNumberOfTheFold_AndNoOther()
    {
        var read = BrowseHeroLayout.Expressions.SelectMany(e => FoldNumber().Matches(e.Text)).Select(match => match.Groups[1].Value).Distinct();

        Assert.Equal(BrowseHeroLayout.ParameterNames.Order(StringComparer.Ordinal), read.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1520)]
    [InlineData(2160)]
    public void EveryNumberOfTheFold_HasAValue(double pageHeight)
    {
        var parameters = BrowseHeroLayout.ParametersFor(BrowseHeroLayout.For(pageHeight));

        Assert.Equal(BrowseHeroLayout.ParameterNames.Order(StringComparer.Ordinal), parameters.Keys.Order(StringComparer.Ordinal));
        Assert.All(parameters.Values, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void AtAPageUnderA1080Window_TheFoldIsAsBefore()
    {
        var parameters = BrowseHeroLayout.ParametersFor(BrowseHeroLayout.For(1000));

        Assert.Equal(150f, parameters["distance"]);
        Assert.Equal(1 - 45f / 80, parameters["shrink"], 0.0001f);
        Assert.Equal(35f, parameters["rise"], 0.0001f);
        Assert.Equal(290f, parameters["cut"]);
        Assert.Equal(288f, parameters["fadeRise"]);
        Assert.Equal(1 - 150f / 440, parameters["fadeShrink"], 0.0001f);
    }

    [Fact]
    public void OnATallerPage_TheFoldFollowsItsSizes()
    {
        var parameters = BrowseHeroLayout.ParametersFor(BrowseHeroLayout.For(1520));

        Assert.Equal(253f, parameters["distance"]);
        Assert.Equal(1 - 55f / 98, parameters["shrink"], 0.0001f);
        Assert.Equal(43f, parameters["rise"], 0.0001f);
        Assert.Equal(458f, parameters["cut"]);
        Assert.Equal(456f, parameters["fadeRise"]);
        Assert.Equal(1 - 185f / 643, parameters["fadeShrink"], 0.0001f);
    }

    [Theory]
    [InlineData(900, 440)]
    [InlineData(1040, 440)]
    [InlineData(1047, 440)]
    [InlineData(1048, 443)]
    [InlineData(1300, 548)]
    [InlineData(1520, 643)]
    [InlineData(1700, 718)]
    [InlineData(2160, 720)]
    public void TheHerosHeight_FollowsThePagesHeight_FromTodaysUpToItsMost(double pageHeight, double restHeight)
    {
        Assert.Equal(restHeight, BrowseHeroLayout.For(pageHeight).RestHeight);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(500)]
    [InlineData(1047)]
    public void APageUpToA1080Window_OrOfNoKnownHeight_HasTodaysSizes(double pageHeight)
    {
        Assert.Equal(BrowseHeroLayout.For(1040), BrowseHeroLayout.For(pageHeight));
    }

    [Theory]
    [InlineData(1712)]
    [InlineData(2160)]
    [InlineData(double.PositiveInfinity)]
    public void ATallEnoughPage_HasTheLargestSizes(double pageHeight)
    {
        var sizes = BrowseHeroLayout.For(pageHeight);

        Assert.Equal((720d, 198d, 491d, 105d, 59d, 396d), (sizes.RestHeight, sizes.CompactHeight, sizes.RowsTop, sizes.HeadingHeight,
            sizes.CompactHeadingHeight, sizes.FadeHeight));
    }

    [Fact]
    public void TheSizes_ChangeOnlyEvery8Px_AndNeverShrink_AsThePageGrows()
    {
        var last = BrowseHeroLayout.For(1040);
        for (double height = 1041; height <= 1720; height++)
        {
            var sizes = BrowseHeroLayout.For(height);
            if (height % 8 != 0)
            {
                Assert.Equal(last, sizes);
            }
            Assert.True(sizes.RestHeight >= last.RestHeight && sizes.CompactHeight >= last.CompactHeight && sizes.RowsTop >= last.RowsTop
                && sizes.HeadingHeight >= last.HeadingHeight && sizes.CompactHeadingHeight >= last.CompactHeadingHeight
                && sizes.FadeHeight >= last.FadeHeight && sizes.Grow >= last.Grow && sizes.TextGrow >= last.TextGrow, $"{height}");
            last = sizes;
        }
    }

    [Fact]
    public void UpToAPageUnderA1080Window_EverySizeIsTodays()
    {
        var sizes = BrowseHeroLayout.For(1040);

        Assert.Equal((440d, 150d, 300d, 150d, 80d, 45d, 242d), (sizes.RestHeight, sizes.CompactHeight, sizes.RowsTop, sizes.CollapseDistance,
            sizes.HeadingHeight, sizes.CompactHeadingHeight, sizes.FadeHeight));
        Assert.Equal((1d, 1d), (sizes.Grow, sizes.TextGrow));
    }

    [Fact]
    public void OnATallerPage_TheRowsFollowTheHero_TheHeaderAndTheLogoGrowHalfAsMuch_AndTheTextLess()
    {
        var sizes = BrowseHeroLayout.For(1520);

        Assert.Equal((643d, 185d, 438d, 253d, 98d, 55d), (sizes.RestHeight, sizes.CompactHeight, sizes.RowsTop, sizes.CollapseDistance,
            sizes.HeadingHeight, sizes.CompactHeadingHeight));
        Assert.Equal((492d, 18d, 17d, 25d), (BrowseHeroLayout.Scaled(400, sizes.Grow), BrowseHeroLayout.Scaled(16, sizes.TextGrow),
            BrowseHeroLayout.Scaled(15, sizes.TextGrow), BrowseHeroLayout.Scaled(22, sizes.TextGrow)));
    }

    [Theory]
    [InlineData(1660, 1000, 1079)]
    [InlineData(1652, 1008, 1074)]
    [InlineData(2300, 1360, 1438)]
    [InlineData(3580, 1520, 1608)]
    [InlineData(800, 1000, 520)]
    [InlineData(2500, 1000, 1100)]
    // Also on a page under a 1080 window: a 2560 x 1080 one's box was a strip of 1625 x 440 before.
    public void TheBackdropBox_TakesItsShareOfTheWidth_ButNeverMoreThanTwoAndAHalfTimesItsHeight(double pageWidth, double pageHeight, double boxWidth)
    {
        Assert.Equal(boxWidth, BrowseHeroLayout.BackdropBoxWidth(pageWidth, BrowseHeroLayout.For(pageHeight)));
    }

    [Theory]
    [InlineData(100, null, 120, 1000, 0)]
    [InlineData(100, null, -120, 1000, 220)]
    [InlineData(500, 380.0, 120, 1000, 260)]
    [InlineData(950, null, -120, 1000, 1000)]
    [InlineData(0, null, 0, 1000, 0)]
    public void AWheelNotch_ScrollsFromWhereTheLastOneWasGoing_WithinTheRows(double offset, double? pending, int delta, double scrollable, double expected)
    {
        Assert.Equal(expected, BrowseHeroLayout.WheelTarget(offset, pending, delta, scrollable));
    }

    [Theory]
    // The first title of a start shows before its width is known, and its fade still runs when the width comes.
    [InlineData(true, false, true, 0, 1200, BackdropStep.LoadArt)]
    [InlineData(true, false, false, 0, 1200, BackdropStep.LoadArt)]
    [InlineData(true, true, false, 0, 1200, BackdropStep.None)]
    [InlineData(true, false, true, 0, 0, BackdropStep.None)]
    [InlineData(false, false, true, 800, 1200, BackdropStep.None)]
    [InlineData(false, false, false, 800, 1200, BackdropStep.DecodeAgain)]
    [InlineData(false, false, false, 1150, 1200, BackdropStep.None)]
    public void AKnownWidth_LoadsAWaitingPicture_EvenDuringItsFade(bool waits, bool folded, bool fading, int decoded, int needed, BackdropStep step)
    {
        Assert.Equal(step, BrowseHeroLayout.StepFor(waits, folded, fading, decoded, needed));
    }

    [Theory]
    [InlineData(1200, 440, 1200)]
    [InlineData(500, 440, 783)]
    [InlineData(1079, 440, 1079)]
    [InlineData(1608, 643, 1608)]
    [InlineData(1000, 720, 1280)]
    [InlineData(double.NaN, 440, 0)]
    [InlineData(0, 440, 0)]
    public void TheHerosBackdrop_IsDecodedAsWideAsItIsShown_OrWiderWhereItsHeightCutsItsSides(double boxWidth, double height, int expected)
    {
        Assert.Equal(expected, BrowseHeroLayout.BackdropDecodeWidth(boxWidth, height));
    }

    [Theory]
    [InlineData(2000, 700, 2000)]
    [InlineData(1000, 700, 1245)]
    [InlineData(1000, double.NaN, 1000)]
    [InlineData(1000, double.PositiveInfinity, 1000)]
    [InlineData(0, 700, 0)]
    public void ABackdropInABoxOfAnyHeight_IsDecodedWideEnoughToFillIt(double boxWidth, double boxHeight, int expected)
    {
        Assert.Equal(expected, BrowseHeroLayout.BackdropDecodeWidth(boxWidth, boxHeight));
    }

    [Theory]
    [InlineData(1000, 1100, false)]
    [InlineData(1000, 1200, true)]
    [InlineData(1000, 600, false)]
    [InlineData(0, 900, true)]
    public void AWiderBackdrop_IsDecodedAgain_OnlyWhenItGrewNotably(int decoded, int needed, bool again)
    {
        Assert.Equal(again, BrowseHeroLayout.DecodesAgain(decoded, needed));
    }
}

public sealed class CrossfadeLayersTests
{
    [Fact]
    public void TheFirstTitle_GoesToALayer_WithNoneToHide()
    {
        var layers = new CrossfadeLayers<string>();

        Assert.Equal(0, layers.Begin("a"));
        Assert.Equal(((int?)null, (string?)null), layers.Complete());
    }

    [Fact]
    public void ANewTitle_FadesInOnTheOtherLayer_AndTheOldOneHidesOnlyAfterwards()
    {
        var layers = new CrossfadeLayers<string>();
        layers.Begin("a");
        layers.Complete();

        Assert.Equal(1, layers.Begin("b"));
        Assert.Equal(1, layers.Front);
        Assert.Equal(((int?)0, (string?)null), layers.Complete());
    }

    [Fact]
    public void ATitleDuringAFade_WaitsForIt_AndOnlyTheLastOneFollows()
    {
        var layers = new CrossfadeLayers<string>();
        layers.Begin("a");
        layers.Complete();
        layers.Begin("b");

        Assert.Null(layers.Begin("c"));
        Assert.Null(layers.Begin("d"));
        var (hide, next) = layers.Complete();

        Assert.Equal(((int?)0, (string?)"d"), (hide, next));
        Assert.Equal(0, layers.Begin(next!));
    }

    [Fact]
    public void AReset_ForgetsTheLayersAndWhatWaits()
    {
        var layers = new CrossfadeLayers<string>();
        layers.Begin("a");
        layers.Begin("b");

        layers.Reset();

        Assert.Equal(-1, layers.Front);
        Assert.Equal(0, layers.Begin("c"));
        Assert.Equal(((int?)null, (string?)null), layers.Complete());
    }
}
