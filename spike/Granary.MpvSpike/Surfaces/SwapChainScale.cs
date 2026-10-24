using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.DXGI;

namespace Granary.MpvSpike.Surfaces;

internal static class SwapChainScale
{
    /// <summary>Physical pixel size of the panel (at least 1x1).</summary>
    public static (int Width, int Height) PixelSize(SwapChainPanel panel) =>
        (Math.Max(1, (int)Math.Round(panel.ActualWidth * panel.CompositionScaleX)),
         Math.Max(1, (int)Math.Round(panel.ActualHeight * panel.CompositionScaleY)));

    /// <summary>
    /// A composition swap chain is sized in physical pixels; the inverse scale maps it back to the
    /// panel's DIPs so it is not drawn magnified at 125 %/150 % display scaling.
    /// </summary>
    public static void ApplyInverseScale(nint swapChain, SwapChainPanel panel)
    {
        Marshal.AddRef(swapChain); // the wrapper below releases on dispose
        using var chain = new IDXGISwapChain(swapChain);
        using var chain2 = chain.QueryInterface<IDXGISwapChain2>();
        chain2.MatrixTransform = Matrix3x2.CreateScale(1f / panel.CompositionScaleX, 1f / panel.CompositionScaleY);
    }
}
