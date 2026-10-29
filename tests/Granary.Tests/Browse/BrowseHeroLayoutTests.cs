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

    [Fact]
    public void EveryParameterAnExpressionNames_HasAValue()
    {
        foreach (var (_, text) in BrowseHeroLayout.Expressions)
        {
            foreach (var name in BrowseHeroLayout.Parameters.Keys.Where(name => text.Contains(name, StringComparison.Ordinal)))
            {
                Assert.True(float.IsFinite(BrowseHeroLayout.Parameters[name]), name);
            }
        }
        Assert.Equal(150f, BrowseHeroLayout.Parameters["distance"]);
        Assert.Equal(1 - 45f / 80, BrowseHeroLayout.Parameters["shrink"], 0.0001f);
        Assert.Equal(35f, BrowseHeroLayout.Parameters["rise"], 0.0001f);
        Assert.Equal(290f, BrowseHeroLayout.Parameters["cut"]);
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
    [InlineData(1200, 1200)]
    [InlineData(500, 783)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, 0)]
    public void TheBackdrop_IsDecodedAsWideAsItIsShown_CutToTheHerosHeight(double boxWidth, int expected)
    {
        Assert.Equal(expected, BrowseHeroLayout.BackdropDecodeWidth(boxWidth));
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
