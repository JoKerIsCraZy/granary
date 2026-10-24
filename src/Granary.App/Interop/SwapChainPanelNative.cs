using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Interop;

/// <summary>ISwapChainPanelNative::SetSwapChain for a WinUI 3 SwapChainPanel, via the COM vtable.</summary>
internal static unsafe class SwapChainPanelNative
{
    // microsoft.ui.xaml.media.dxinterop.h (WinUI 3; differs from the UWP interface id).
    private static readonly Guid Iid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    /// <summary>Sets (or with 0 clears) the panel's swap chain. The panel keeps its own reference.</summary>
    public static void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        nint unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        Guid iid = Iid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint native));
        try
        {
            // vtable: QueryInterface, AddRef, Release, SetSwapChain
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)native)[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}
