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
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out var device)
            .CheckError();
        using (device)
        using (var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false))
        using (var swapChain = factory.CreateSwapChainForComposition(device, new SwapChainDescription1
               {
                   Width = 64,
                   Height = 64,
                   Format = Format.B8G8R8A8_UNorm,
                   SampleDescription = new SampleDescription(1, 0),
                   BufferUsage = Usage.RenderTargetOutput,
                   BufferCount = 2,
                   Scaling = Scaling.Stretch,
                   SwapEffect = SwapEffect.FlipSequential,
                   AlphaMode = AlphaMode.Ignore,
               }))
        {
            DxgiSwapChain.SetInverseScale(swapChain.NativePointer, 2f, 4f);

            using var chain2 = swapChain.QueryInterface<IDXGISwapChain2>();
            var matrix = chain2.MatrixTransform;
            Assert.Equal(0.5f, matrix.M11, 4);
            Assert.Equal(0.25f, matrix.M22, 4);
            Assert.Equal(0f, matrix.M12, 4);
            Assert.Equal(0f, matrix.M31, 4);
        }
    }
}
