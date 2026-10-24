namespace Granary.Player;

/// <summary>
/// The video side of the engine for a XAML host: mpv owns the composition swap chain, the host sets it
/// on a SwapChainPanel and reports the panel's size in physical pixels.
/// </summary>
public interface ICompositionVideoOutput
{
    /// <summary>The <c>IDXGISwapChain*</c> mpv created, or 0 before the video output exists.</summary>
    nint SwapChain { get; }

    /// <summary>Raised (on a background thread) when <see cref="SwapChain"/> changes.</summary>
    event EventHandler? SwapChainChanged;

    void Resize(int widthPixels, int heightPixels);
}
