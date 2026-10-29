namespace Granary.Core.ViewModels.Cards;

/// <summary>Where focus is on a TMDB card or its Request, as far as Request cares (WinUI's FocusState).</summary>
public enum CardFocus
{
    None,
    Pointer,
    Keyboard,
    Programmatic,
}

/// <summary>
/// How a TMDB card's Request presents itself, decided from where the viewer is; the card applies it. It shows under a
/// pointer that may request (a mouse, or a touch or pen on a card that already had focus), takes that pointer only once
/// armed (shown past its fade), shows with keyboard focus on the card, the web's focus-within, and stays in view while
/// its request is on its way. Focus the card got from code, as when a flyout closes or Back returns, does not show it: a
/// tap could then request at once. Unseen, it is out of the accessibility tree and off, so nothing invokes it unawares;
/// when it is about to go off while it holds focus (its request started, or it went), focus goes back to the card first.
/// </summary>
/// <param name="Shown">Drawn (opacity 1).</param>
/// <param name="Enabled">Takes Enter, Space and clicks: shown and not sending.</param>
/// <param name="TabStop">Tab reaches it: shown for the keyboard and not sending.</param>
/// <param name="TakesPointer">Hit-testable.</param>
/// <param name="InAccessibilityTree">Screen readers and automation see it.</param>
/// <param name="MoveFocusToCard">It holds focus and is about to go off: the card takes focus first, or it would be lost.</param>
/// <param name="Arm">Under the pointer and not yet armed: arm it once it has shown long enough.</param>
public readonly record struct CardRequestPill(bool Shown, bool Enabled, bool TabStop, bool TakesPointer, bool InAccessibilityTree, bool MoveFocusToCard,
    bool Arm)
{
    /// <param name="offered">The card can be requested, or its request is on its way.</param>
    /// <param name="requesting">Its request is on its way.</param>
    /// <param name="card">Focus on the card's own button.</param>
    /// <param name="pill">Focus on Request.</param>
    /// <param name="pointerOver">A pointer that may request is over the card.</param>
    /// <param name="armed">Request has shown under that pointer long enough to take it.</param>
    public static CardRequestPill Decide(bool offered, bool requesting, CardFocus card, CardFocus pill, bool pointerOver, bool armed)
    {
        // Keyboard focus on the card, or focus Request itself holds from Tab or code (a pointer's press on it ends in a click).
        bool keyboard = offered && (card == CardFocus.Keyboard || pill is CardFocus.Keyboard or CardFocus.Programmatic);
        bool pointer = offered && pointerOver;
        bool shown = offered && (requesting || keyboard || pointer);
        bool enabled = shown && !requesting;
        return new CardRequestPill(
            Shown: shown,
            Enabled: enabled,
            TabStop: keyboard && !requesting,
            TakesPointer: enabled && (keyboard || (pointer && armed)),
            InAccessibilityTree: shown,
            MoveFocusToCard: pill != CardFocus.None && !enabled,
            Arm: pointer && !requesting && !armed);
    }
}
