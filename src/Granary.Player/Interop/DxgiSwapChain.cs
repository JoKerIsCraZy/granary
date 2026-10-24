using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Granary.Player.Interop;

/// <summary>The one DXGI call the composition host needs, made through the COM vtable directly.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class DxgiSwapChain
{
    private static readonly Guid SwapChain2Iid = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    // IUnknown (3) + IDXGIObject (4) + IDXGIDeviceSubObject (1) + IDXGISwapChain (10) +
    // IDXGISwapChain1 (11) = 29; IDXGISwapChain2: SetSourceSize, GetSourceSize,
    // SetMaximumFrameLatency, GetMaximumFrameLatency, GetFrameLatencyWaitableObject, SetMatrixTransform.
    internal const int SetMatrixTransformSlot = 34;

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
