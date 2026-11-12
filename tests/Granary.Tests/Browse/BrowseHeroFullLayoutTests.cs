using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

/// <summary>
/// Settings → Navigation &amp; Cards → Hero size, Full: the hero is as tall as the banner of Silo's web (66 % of the page, at
/// least 380 px and at most 760 px), the rows begin under it, its text sits at its foot, and it folds into the same compact
/// header as the compact hero.
/// </summary>
public sealed class BrowseHeroFullLayoutTests
{
    private static BrowseHeroSizes Full(double pageHeight, bool banner = false) => BrowseHeroLayout.For(pageHeight, banner, BrowseHeroSize.Full);

    [Theory]
    [InlineData(700, 459)]
    [InlineData(1040, 686)]
    [InlineData(1300, 760)]
    [InlineData(1552, 760)]
    public void TheFullHero_TakesTwoThirdsOfThePage_AsTheWebsBanner(double pageHeight, double restHeight)
    {
        Assert.Equal(restHeight, Full(pageHeight).RestHeight);
    }

    [Theory]
    [InlineData(0, 380)]
    [InlineData(500, 380)]
    [InlineData(575, 380)]
    [InlineData(584, 385)]
    [InlineData(1144, 755)]
    [InlineData(1152, 760)]
    [InlineData(2160, 760)]
    [InlineData(double.PositiveInfinity, 760)]
    [InlineData(double.NaN, 380)]
    [InlineData(double.NegativeInfinity, 380)]
    [InlineData(-1, 380)]
    public void TheFullHero_IsNeverLowerThan380_NorTallerThan760(double pageHeight, double restHeight)
    {
        Assert.Equal(restHeight, Full(pageHeight).RestHeight);
    }

    [Theory]
    [InlineData(700)]
    [InlineData(1040)]
    [InlineData(1300)]
    [InlineData(1552)]
    public void TheRows_BeginUnderTheFullHero_AndItsFadeIsTheWebsTwoThirds(double pageHeight)
    {
        var sizes = Full(pageHeight);

        Assert.Equal(sizes.RestHeight, sizes.RowsTop);
        Assert.Equal(Math.Round(sizes.RestHeight * 0.66, MidpointRounding.AwayFromZero), sizes.FadeHeight);
    }

    [Theory]
    [InlineData(1664, 1040)]
    [InlineData(800, 700)]
    // A 3840 x 1600 window: the compact hero's box stops at two and a half times its height, the full one's does not.
    [InlineData(3580, 1552)]
    public void TheFullHerosBackdrop_RunsOverItsWholeWidth_AsTheWebsBanner(double pageWidth, double pageHeight)
    {
        Assert.Equal(pageWidth, BrowseHeroLayout.BackdropBoxWidth(pageWidth, Full(pageHeight)));
        Assert.True(Full(pageHeight).IsFull);
        Assert.False(BrowseHeroLayout.For(pageHeight).IsFull);
    }

    [Theory]
    // The box, then the picture: its size, and where its top left lies from the box's.
    // Wider than 16:9: as wide as the box, a fifth of what its height loses cut at the top.
    [InlineData(1079, 440, 1079, 607, 0, -33)]
    [InlineData(1664, 686, 1664, 936, 0, -50)]
    [InlineData(3580, 760, 3580, 2014, 0, -251)]
    // Narrower: as high as the box, its sides cut evenly.
    [InlineData(1000, 720, 1280, 720, -140, 0)]
    [InlineData(800, 459, 816, 459, -8, 0)]
    public void ABackdrop_FillsItsBox_ShowingItsUpperPart_AsTheWebsHeroes(double boxWidth, double boxHeight, double width, double height,
        double left, double top)
    {
        Assert.Equal(new BackdropPlace(width, height, left, top), BrowseHeroLayout.PlaceBackdrop(boxWidth, boxHeight));
    }

    [Fact]
    public void ABackdrop_CoversItsBox_WhateverTheBoxesSize()
    {
        for (double boxWidth = 300; boxWidth <= 3900; boxWidth += 37)
        {
            for (double boxHeight = 150; boxHeight <= 760; boxHeight += 61)
            {
                var place = BrowseHeroLayout.PlaceBackdrop(boxWidth, boxHeight);

                Assert.True(place.Left <= 0 && place.Top <= 0 && place.Left + place.Width >= boxWidth && place.Top + place.Height >= boxHeight,
                    $"{boxWidth} x {boxHeight}: {place}");
                // The picture keeps its shape to within the pixel its size is rounded up by.
                Assert.InRange(place.Width - place.Height * 16 / 9, -2, 2);
            }
        }
    }

    [Theory]
    [InlineData(0, 440)]
    [InlineData(1000, 0)]
    [InlineData(double.NaN, 440)]
    [InlineData(1000, double.PositiveInfinity)]
    [InlineData(-5, 440)]
    public void ABoxWithoutASize_PlacesNoBackdrop(double boxWidth, double boxHeight)
    {
        Assert.Equal(default, BrowseHeroLayout.PlaceBackdrop(boxWidth, boxHeight));
    }

    [Theory]
    // A 1920 x 1040 window: the compact hero's box is 1100 wide, the full one's the window's.
    [InlineData(1920, 1040, BrowseHeroSize.Compact, 1100)]
    [InlineData(1920, 1040, BrowseHeroSize.Full, 1920)]
    // A 3840 x 1560 one.
    [InlineData(3840, 1560, BrowseHeroSize.Compact, 1650)]
    [InlineData(3840, 1560, BrowseHeroSize.Full, 3840)]
    // A narrow, tall one: the hero's height cuts the picture's sides, so it is drawn wider than the window.
    [InlineData(1000, 1200, BrowseHeroSize.Full, 1352)]
    [InlineData(0, 1040, BrowseHeroSize.Full, 0)]
    [InlineData(double.NaN, 1040, BrowseHeroSize.Full, 0)]
    [InlineData(1920, double.NaN, BrowseHeroSize.Full, 1920)]
    public void TheWidthTheHeroDrawsItsBackdropAt_FollowsTheWindowAndTheHerosSize(double windowWidth, double windowHeight, BrowseHeroSize size,
        double drawn)
    {
        Assert.Equal(drawn, BrowseHeroLayout.BackdropDrawnWidth(windowWidth, windowHeight, size));
    }

    [Theory]
    [InlineData(700)]
    [InlineData(1040)]
    [InlineData(1300)]
    [InlineData(1552)]
    [InlineData(2160)]
    public void TheFullHero_FoldsIntoTheCompactHerosHeader_WithItsLogoAndItsType(double pageHeight)
    {
        var compact = BrowseHeroLayout.For(pageHeight);
        var full = Full(pageHeight);

        Assert.Equal((compact.CompactHeight, compact.HeadingHeight, compact.CompactHeadingHeight, compact.Grow, compact.TextGrow),
            (full.CompactHeight, full.HeadingHeight, full.CompactHeadingHeight, full.Grow, full.TextGrow));
    }

    [Theory]
    // The page's height, then the full hero's: its height, its header, the rows' top and the fold's length; then the same of
    // the compact one.
    [InlineData(700, 459, 150, 459, 309, 440, 150, 300, 150)]
    [InlineData(1040, 686, 150, 686, 536, 440, 150, 300, 150)]
    [InlineData(1300, 760, 168, 760, 592, 548, 168, 374, 206)]
    [InlineData(1552, 760, 187, 760, 573, 657, 187, 448, 261)]
    public void TheFullHerosSizes_NextToTheCompactOnes(double pageHeight, double rest, double header, double rowsTop, double fold,
        double compactRest, double compactHeader, double compactRowsTop, double compactFold)
    {
        var full = Full(pageHeight);
        var compact = BrowseHeroLayout.For(pageHeight);

        Assert.Equal((rest, header, rowsTop, fold), (full.RestHeight, full.CompactHeight, full.RowsTop, full.CollapseDistance));
        Assert.Equal((compactRest, compactHeader, compactRowsTop, compactFold),
            (compact.RestHeight, compact.CompactHeight, compact.RowsTop, compact.CollapseDistance));
    }

    [Fact]
    public void TheFullHerosSizes_ChangeOnlyEvery8Px_AndNeverShrink_AsThePageGrows()
    {
        var last = Full(400);
        for (double height = 401; height <= 1720; height++)
        {
            var sizes = Full(height);
            if (height % 8 != 0)
            {
                Assert.Equal(last, sizes);
            }
            Assert.True(sizes.RestHeight >= last.RestHeight && sizes.CompactHeight >= last.CompactHeight && sizes.RowsTop >= last.RowsTop
                && sizes.CollapseDistance > 0 && sizes.FadeHeight >= last.FadeHeight, $"{height}");
            last = sizes;
        }
    }

    [Fact]
    public void TheFullHerosText_SitsAtItsFoot_AsTheWebsBanner_64PxAboveItsBottom()
    {
        Assert.Equal(64, Full(1040).TextBottom);
        Assert.Equal(64, Full(1552).TextBottom);
    }

    [Fact]
    public void TheCompactHerosText_HangsFromTheTop_AndItsBannersButtonsStay26PxAboveTheRows()
    {
        foreach (bool banner in new[] { false, true })
        {
            var sizes = BrowseHeroLayout.For(1040, banner);

            Assert.Null(sizes.TextBottom);
            Assert.Equal(26, sizes.ButtonsBottom);
        }
    }

    [Fact]
    public void UnderABanner_TheFullHeroIsNoTaller_ItsButtonsSitAtItsFoot_AndItsTextOverThem()
    {
        var hero = Full(1040);
        var banner = Full(1040, banner: true);

        // The buttons 64 px above the hero's bottom, where the rows begin; the text the web's 28 px above the 44 px buttons.
        Assert.Equal(64, banner.ButtonsBottom);
        Assert.Equal(64 + 44 + 28, banner.TextBottom);
        Assert.Equal(hero with { TextBottom = 136 }, banner);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(268, false)]
    [InlineData(269, true)]
    public void TheFullHero_FoldsPastHalfItsLongerWay(double offset, bool folded)
    {
        Assert.Equal(folded, BrowseHeroLayout.IsFolded(offset, Full(1040)));
    }

    [Fact]
    public void TheFullHerosFold_RunsFromItsHeightToTheHeader_AndItsBackdropEndsWhereTheRowsBegin()
    {
        var parameters = BrowseHeroLayout.ParametersFor(Full(1040), lift: 395);

        Assert.Equal(536f, parameters["distance"]);
        // The backdrop is cut as far as the rows scroll, so its foot stays on the first row's top all the way.
        Assert.Equal(536f, parameters["cut"]);
        Assert.Equal(534f, parameters["fadeRise"]);
        Assert.Equal(1 - 150f / 686, parameters["fadeShrink"], 0.0001f);
        Assert.Equal(1 - 45f / 80, parameters["shrink"], 0.0001f);
        Assert.Equal(35f, parameters["rise"], 0.0001f);
        Assert.Equal(395f, parameters["lift"]);
    }

    [Theory]
    // A title's logo, details and three lines of overview are 187 px high, a slide's with its eyebrow 213 px; the header's
    // text begins 40 px down.
    [InlineData(1040, false, 187, 395)]
    [InlineData(1040, true, 213, 297)]
    [InlineData(700, false, 187, 168)]
    [InlineData(1552, false, 187, 469)]
    // A title without an overview is lower, and has further to go.
    [InlineData(1040, false, 111, 471)]
    public void TheFullHerosText_GoesUpWithTheRows_FromItsFootToTheHeader(double pageHeight, bool banner, double textHeight, double lift)
    {
        Assert.Equal(lift, BrowseHeroLayout.TextLift(Full(pageHeight, banner), textHeight, headerTop: 40));
    }

    [Fact]
    public void TextTooTallForASmallHero_StaysWhereItIs()
    {
        Assert.Equal(0, BrowseHeroLayout.TextLift(Full(500, banner: true), 260, headerTop: 40));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCompactHerosText_NeverMoves_AndItsFoldHasNoLift(bool banner)
    {
        var sizes = BrowseHeroLayout.For(1040, banner);

        Assert.Equal(0, BrowseHeroLayout.TextLift(sizes, 187, headerTop: 40));
        Assert.Equal(0f, BrowseHeroLayout.ParametersFor(sizes)["lift"]);
    }

    [Fact]
    public void TheTextsWay_FollowsTheScrollItself_UpToItsLift()
    {
        var text = Assert.Single(BrowseHeroLayout.Expressions, e => e.Target == "Texts.Translation.Y").Text;

        Assert.Equal("Clamp(scroll.Translation.Y, -p.lift, 0)", text);
    }
}
