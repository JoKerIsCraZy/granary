using System.Runtime.InteropServices;
using Granary.MpvSpike.Interop;
using Microsoft.UI.Xaml;

namespace Granary.MpvSpike.Surfaces;

/// <summary>
/// Variant B: a Win32 child window inside the WinUI window, handed to mpv via --wid. mpv renders
/// with its normal d3d11 window backend. XAML cannot draw over a child HWND ("airspace").
/// </summary>
internal sealed unsafe class HwndSurface(nint parentHwnd, FrameworkElement host, bool placeBelowXaml) : IVideoSurface
{
    private const string ClassName = "GranarySpikeVideoHost";
    private static bool _classRegistered;
    private nint _child;
    private (int X, int Y, int W, int H) _bounds;

    public string Name => placeBelowXaml ? "hwnd (below XAML)" : "hwnd";

    public void ConfigureBeforeInit(MpvPlayer player)
    {
        RegisterClassOnce();
        _child = Native.CreateWindowEx(0, ClassName, "", Native.WsChild | Native.WsVisible | Native.WsClipSiblings,
            0, 0, 16, 16, parentHwnd, 0, Native.GetModuleHandle(null), 0);
        if (_child == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        }
        UpdateLayout();
        player.SetOption("wid", ((long)_child).ToString());
        player.SetOption("vo", "gpu-next");
        player.SetOption("gpu-api", "d3d11");
        player.SetOption("force-window", "yes");
    }

    public void AttachAfterInit(MpvPlayer player)
    {
        SpikeLog.Write("surface", $"child hwnd 0x{_child:X} placeBelowXaml={placeBelowXaml}");
    }

    public void UpdateLayout()
    {
        if (_child == 0 || host.XamlRoot is null)
        {
            return;
        }
        double scale = host.XamlRoot.RasterizationScale;
        var origin = host.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var bounds = ((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
            Math.Max(1, (int)Math.Round(host.ActualWidth * scale)), Math.Max(1, (int)Math.Round(host.ActualHeight * scale)));
        if (bounds == _bounds)
        {
            return;
        }
        _bounds = bounds;
        nint insertAfter = placeBelowXaml ? Native.HwndBottom : Native.HwndTop;
        Native.SetWindowPos(_child, insertAfter, bounds.Item1, bounds.Item2, bounds.Item3, bounds.Item4, Native.SwpNoActivate);
    }

    public string Describe() => $"{Name} {_bounds.W}x{_bounds.H}";

    private static void RegisterClassOnce()
    {
        if (_classRegistered)
        {
            return;
        }
        fixed (char* name = ClassName)
        {
            var wc = new Native.WndClassEx
            {
                CbSize = (uint)sizeof(Native.WndClassEx),
                WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                Instance = Native.GetModuleHandle(null),
                ClassName = (nint)name,
            };
            if (Native.RegisterClassEx(ref wc) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
            }
        }
        _classRegistered = true;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam) => Native.DefWindowProc(hwnd, msg, wParam, lParam);

    public void Dispose()
    {
        if (_child != 0)
        {
            Native.DestroyWindow(_child);
            _child = 0;
        }
    }
}
