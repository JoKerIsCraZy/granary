namespace Granary.Core.ViewModels.Cards;

/// <summary>What a card grid shows of its list, as the view reports it while it scrolls.</summary>
/// <param name="First">The first card in view.</param>
/// <param name="Last">The last card in view.</param>
/// <param name="TopRowEnd">The last card of the top row (the first row at least half in view).</param>
/// <param name="IsAtEnd">The scroller is at its end: nothing below can scroll up into the top row.</param>
public sealed record GridViewport(int First, int Last, int TopRowEnd, bool IsAtEnd);
