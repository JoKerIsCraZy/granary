namespace Granary.Player;

/// <summary>A size in physical pixels; empty (0 × 0) when there is nothing to measure.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width < 1 || Height < 1;
}
