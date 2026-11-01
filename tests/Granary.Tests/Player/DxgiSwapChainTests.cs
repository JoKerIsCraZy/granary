using Granary.Player;
using Granary.Player.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Granary.Tests.Player;

/// <summary>
/// Checks the hand-counted vtable slot of IDXGISwapChain2::SetMatrixTransform against Vortice's
/// generated binding, on a WARP device so no GPU is needed.
/// </summary>
public sealed class DxgiSwapChainTests
{
    [Fact]
    public void SetInverseScale_WritesTheInverseMatrix()
    {
        WithCompositionSwapChain(64, 64, swapChain =>
        {
            DxgiSwapChain.SetInverseScale(swapChain.NativePointer, 2f, 4f);

            using var chain2 = swapChain.QueryInterface<IDXGISwapChain2>();
            var matrix = chain2.MatrixTransform;
            Assert.Equal(0.5f, matrix.M11, 4);
            Assert.Equal(0.25f, matrix.M22, 4);
            Assert.Equal(0f, matrix.M12, 4);
            Assert.Equal(0f, matrix.M31, 4);
        });
    }

    [Fact]
    public void BufferSize_FollowsTheSwapChainThroughAResize()
    {
        WithCompositionSwapChain(64, 48, swapChain =>
        {
            Assert.Equal(new PixelSize(64, 48), DxgiSwapChain.BufferSize(swapChain.NativePointer));

            swapChain.ResizeBuffers(2, 160, 90, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();

            Assert.Equal(new PixelSize(160, 90), DxgiSwapChain.BufferSize(swapChain.NativePointer));
        });
    }

    [Fact]
    public void BufferSize_OfNoSwapChainIsEmpty() =>
        Assert.Equal(default, DxgiSwapChain.BufferSize(0));

    [Fact]
    public void PresentCount_GoesUpWithEachPresent()
    {
        WithCompositionSwapChain(64, 48, swapChain =>
        {
            uint before = DxgiSwapChain.PresentCount(swapChain.NativePointer);

            swapChain.Present(0, PresentFlags.None).CheckError();

            Assert.Equal(before + 1, DxgiSwapChain.PresentCount(swapChain.NativePointer));
        });
    }

    [Fact]
    public void PresentCount_OfNoSwapChainIsZero() =>
        Assert.Equal(0u, DxgiSwapChain.PresentCount(0));

    private static void WithCompositionSwapChain(uint width, uint height, Action<IDXGISwapChain1> test)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out var device)
            .CheckError();
        using (device)
        using (var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false))
        using (var swapChain = factory.CreateSwapChainForComposition(device, new SwapChainDescription1
               {
                   Width = width,
                   Height = height,
                   Format = Format.B8G8R8A8_UNorm,
                   SampleDescription = new SampleDescription(1, 0),
                   BufferUsage = Usage.RenderTargetOutput,
                   BufferCount = 2,
                   Scaling = Scaling.Stretch,
                   SwapEffect = SwapEffect.FlipSequential,
                   AlphaMode = AlphaMode.Ignore,
               }))
        {
            test(swapChain);
        }
    }
}
