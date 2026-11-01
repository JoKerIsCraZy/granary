using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Granary.Player.Interop;

/// <summary>The DXGI calls the composition host needs, made through the COM vtable directly.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class DxgiSwapChain
{
    private static readonly Guid SwapChain1Iid = new("790a45f7-0d42-4876-983a-0a55cfe6f4aa");
    private static readonly Guid SwapChain2Iid = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    // IUnknown (3) + IDXGIObject (4) + IDXGIDeviceSubObject (1) = 8; IDXGISwapChain: Present, GetBuffer,
    // SetFullscreenState, GetFullscreenState, GetDesc, ResizeBuffers, ResizeTarget, GetContainingOutput,
    // GetFrameStatistics, GetLastPresentCount.
    internal const int GetLastPresentCountSlot = 17;

    // ... + IDXGISwapChain (10) = 18: IDXGISwapChain1's first method, GetDesc1.
    internal const int GetDesc1Slot = 18;

    // ... + IDXGISwapChain1 (11) = 29; IDXGISwapChain2: SetSourceSize, GetSourceSize,
    // SetMaximumFrameLatency, GetMaximumFrameLatency, GetFrameLatencyWaitableObject, SetMatrixTransform.
    internal const int SetMatrixTransformSlot = 34;

    // DXGI_SWAP_CHAIN_DESC1: Width, Height, Format, Stereo, SampleDesc (2), BufferUsage, BufferCount, Scaling,
    // SwapEffect, AlphaMode, Flags; twelve 32-bit fields.
    private const int Desc1Fields = 12;

    /// <summary>
    /// How many times mpv has presented into the swap chain; 0 without one. A present after a resize is the first
    /// picture the screen shows at the new size.
    /// </summary>
    public static uint PresentCount(nint swapChain)
    {
        if (swapChain == 0)
        {
            return 0;
        }
        uint count;
        var getLastPresentCount = (delegate* unmanaged[Stdcall]<nint, uint*, int>)(*(nint**)swapChain)[GetLastPresentCountSlot];
        return getLastPresentCount(swapChain, &count) < 0 ? 0 : count;
    }

    /// <summary>
    /// The size of the swap chain's buffers, as mpv last sized them; empty without a swap chain or when DXGI cannot
    /// say. It only reads the swap chain's description; like <see cref="SetInverseScale"/>, it is called from the UI
    /// thread while mpv renders on its own.
    /// </summary>
    public static PixelSize BufferSize(nint swapChain)
    {
        if (swapChain == 0)
        {
            return default;
        }
        Guid iid = SwapChain1Iid;
        if (Marshal.QueryInterface(swapChain, in iid, out nint chain1) < 0)
        {
            return default;
        }
        try
        {
            uint* desc = stackalloc uint[Desc1Fields];
            var getDesc1 = (delegate* unmanaged[Stdcall]<nint, uint*, int>)(*(nint**)chain1)[GetDesc1Slot];
            return getDesc1(chain1, desc) < 0 ? default : new PixelSize((int)desc[0], (int)desc[1]);
        }
        finally
        {
            Marshal.Release(chain1);
        }
    }

    /// <summary>
    /// A composition swap chain is sized in physical pixels; scaling it by the inverse of the panel's
    /// composition scale maps it back to the panel's DIPs at 125 %/150 % display scaling.
    /// </summary>
    public static void SetInverseScale(nint swapChain, float scaleX, float scaleY)
    {
        if (swapChain == 0 || scaleX <= 0 || scaleY <= 0)
        {
            return;
        }
        Guid iid = SwapChain2Iid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(swapChain, in iid, out nint chain2));
        try
        {
            float* matrix = stackalloc float[6] { 1f / scaleX, 0f, 0f, 1f / scaleY, 0f, 0f };
            var setMatrixTransform = (delegate* unmanaged[Stdcall]<nint, float*, int>)(*(nint**)chain2)[SetMatrixTransformSlot];
            Marshal.ThrowExceptionForHR(setMatrixTransform(chain2, matrix));
        }
        finally
        {
            Marshal.Release(chain2);
        }
    }
}
