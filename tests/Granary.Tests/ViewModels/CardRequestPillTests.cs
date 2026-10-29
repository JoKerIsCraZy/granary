using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

/// <summary>How a TMDB card's Request presents itself for the pointer, the keyboard and screen readers.</summary>
public sealed class CardRequestPillTests
{
    private static CardRequestPill Decide(bool offered = true, bool requesting = false, CardFocus card = CardFocus.None, CardFocus pill = CardFocus.None,
        bool pointerOver = false, bool armed = false) =>
        CardRequestPill.Decide(offered, requesting, card, pill, pointerOver, armed);

    [Fact]
    public void Enter_OnThePill_HandsFocusToTheCard_BeforeItGoesOff()
    {
        var sending = Decide(requesting: true, pill: CardFocus.Keyboard);

        Assert.True(sending.MoveFocusToCard);
        Assert.False(sending.Enabled);
        Assert.True(sending.Shown);
    }

    [Fact]
    public void TheCardThatTookFocus_KeepsIt_WhileSending_AndAfter()
    {
        var sending = Decide(requesting: true, card: CardFocus.Keyboard);
        Assert.False(sending.MoveFocusToCard);
        Assert.True(sending.Shown);
        Assert.False(sending.TabStop);
        Assert.False(sending.TakesPointer);

        var requested = Decide(offered: false, card: CardFocus.Keyboard);
        Assert.False(requested.MoveFocusToCard);
        Assert.False(requested.Shown);
    }

    [Fact]
    public void KeyboardFocusOnTheCard_ShowsIt_ForTab()
    {
        var pill = Decide(card: CardFocus.Keyboard);

        Assert.Equal((true, true, true, true, true), (pill.Shown, pill.Enabled, pill.TabStop, pill.TakesPointer, pill.InAccessibilityTree));
    }

    [Theory]
    [InlineData(CardFocus.Programmatic)]
    [InlineData(CardFocus.Pointer)]
    public void FocusTheCardGotFromCodeOrAPointer_DoesNotShowIt(CardFocus focus)
    {
        var pill = Decide(card: focus);

        Assert.False(pill.Shown);
        Assert.False(pill.Enabled);
        Assert.False(pill.TakesPointer);
        Assert.False(pill.TabStop);
    }

    [Theory]
    [InlineData(CardFocus.Keyboard)]
    [InlineData(CardFocus.Programmatic)]
    public void FocusOnThePillItself_KeepsItShown(CardFocus focus)
    {
        var pill = Decide(pill: focus);

        Assert.True(pill.Shown);
        Assert.True(pill.TabStop);
        Assert.False(pill.MoveFocusToCard);
    }

    [Fact]
    public void APointer_SeesItAtOnce_ButTakesItOnlyOnceArmed()
    {
        var fresh = Decide(pointerOver: true);
        Assert.True(fresh.Shown);
        Assert.False(fresh.TakesPointer);
        Assert.True(fresh.Arm);
        Assert.False(fresh.TabStop);

        var armed = Decide(pointerOver: true, armed: true);
        Assert.True(armed.TakesPointer);
        Assert.False(armed.Arm);
    }

    // A pointer coming over a card the keyboard already shows Request on waits for the arming too: its click on the way
    // in opens the card.
    [Fact]
    public void APointerOverACardWithKeyboardFocus_StillWaitsForTheArming()
    {
        var coming = Decide(card: CardFocus.Keyboard, pointerOver: true);
        Assert.True(coming.Shown);
        Assert.True(coming.TabStop);
        Assert.False(coming.TakesPointer);
        Assert.True(coming.Arm);

        Assert.True(Decide(card: CardFocus.Keyboard, pointerOver: true, armed: true).TakesPointer);
    }

    [Fact]
    public void Unseen_IsOff_AndOutOfTheAccessibilityTree()
    {
        var pill = Decide();

        Assert.Equal((false, false, false, false, false), (pill.Shown, pill.Enabled, pill.TabStop, pill.TakesPointer, pill.InAccessibilityTree));
    }

    [Fact]
    public void Sending_ShowsIt_ButTakesNothing()
    {
        var pill = Decide(requesting: true, pointerOver: true, armed: true);

        Assert.True(pill.Shown);
        Assert.True(pill.InAccessibilityTree);
        Assert.False(pill.Enabled);
        Assert.False(pill.TakesPointer);
        Assert.False(pill.Arm);
    }

    [Fact]
    public void APillThePointerLeft_WhileItHeldFocus_HandsFocusToTheCard()
    {
        var pill = Decide(pill: CardFocus.Pointer);

        Assert.False(pill.Shown);
        Assert.True(pill.MoveFocusToCard);
    }

    [Fact]
    public void ACardThatCannotBeRequested_ShowsNothing_WhateverHasFocus()
    {
        var pill = Decide(offered: false, card: CardFocus.Keyboard, pointerOver: true, armed: true);

        Assert.False(pill.Shown);
        Assert.False(pill.Enabled);
        Assert.False(pill.Arm);
    }
}
