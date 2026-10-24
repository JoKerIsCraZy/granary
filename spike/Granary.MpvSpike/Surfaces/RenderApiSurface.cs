using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Granary.MpvSpike.Interop;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Granary.MpvSpike.Surfaces;

/// <summary>
/// Variant A: libmpv's render API with the D3D11 backend (mpv master, render_d3d11.h). The app owns
/// the device and a composition swap chain on the SwapChainPanel; mpv renders into its back buffer.
/// </summary>
internal sealed unsafe class RenderApiSurface(SwapChainPanel panel, bool hdrOutput) : IVideoSurface
{
    private const int BufferCount = 2;
    private readonly AutoResetEvent _wake = new(false);
    private ID3D11Device? _device;
    private IDXGISwapChain1? _swapChain;
    private nint _renderContext;
    private Thread? _renderThread;
    private GCHandle _self;
    private volatile bool _running;
    private (int W, int H) _size;
    private (int W, int H) _pendingSize;
    private volatile bool _resizePending;
    private long _framesRendered;

    private Format BufferFormat => hdrOutput ? Format.R10G10B10A2_UNorm : Format.B8G8R8A8_UNorm;

    public string Name => "render-api (d3d11)";

    public void ConfigureBeforeInit(MpvPlayer player)
    {
        player.SetOption("vo", "libmpv");
        if (hdrOutput)
        {
            player.SetOption("target-trc", "pq");
            player.SetOption("target-prim", "bt.2020");
        }
    }

    public void AttachAfterInit(MpvPlayer player)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device).CheckError();

        _size = SwapChainScale.PixelSize(panel);
        if (_size.W < 16 || _size.H < 16)
        {
            _size = (1280, 720);
        }
        using (var dxgiDevice = _device!.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)_size.W,
                Height = (uint)_size.H,
                Format = BufferFormat,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = BufferCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, desc);
            SpikeLog.Write("surface", $"render-api adapter: {adapter.Description.Description}");
        }
        if (hdrOutput)
        {
            using var chain3 = _swapChain.QueryInterface<IDXGISwapChain3>();
            chain3.SetColorSpace1(ColorSpaceType.RgbFullG2084NoneP2020);
        }
        Native.SetSwapChain(panel, _swapChain.NativePointer);
        SwapChainScale.ApplyInverseScale(_swapChain.NativePointer, panel);

        nint apiType = Marshal.StringToCoTaskMemUTF8("d3d11");
        nint initParams = Marshal.AllocHGlobal(sizeof(nint));
        nint advancedControl = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteIntPtr(initParams, _device.NativePointer);
            Marshal.WriteInt32(advancedControl, 1);
            LibMpv.MpvRenderParam[] parameters =
            [
                new() { Type = LibMpv.RenderParamApiType, Data = apiType },
                new() { Type = LibMpv.RenderParamD3D11InitParams, Data = initParams },
                new() { Type = LibMpv.RenderParamAdvancedControl, Data = advancedControl },
                new() { Type = LibMpv.RenderParamInvalid, Data = 0 },
            ];
            LibMpv.Check(LibMpv.RenderContextCreate(out _renderContext, player.Handle, parameters), "mpv_render_context_create");
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
            Marshal.FreeHGlobal(initParams);
            Marshal.FreeHGlobal(advancedControl);
        }

        _self = GCHandle.Alloc(this);
        LibMpv.RenderContextSetUpdateCallback(_renderContext, &OnMpvUpdate, GCHandle.ToIntPtr(_self));
        _running = true;
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "mpv-render" };
        _renderThread.Start();
        SpikeLog.Write("surface", $"render context created, swap chain {_size.W}x{_size.H} {BufferFormat}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMpvUpdate(nint context)
    {
        if (GCHandle.FromIntPtr(context).Target is RenderApiSurface surface)
        {
            surface._wake.Set();
        }
    }

    private void RenderLoop()
    {
        while (_running)
        {
            _wake.WaitOne();
            if (!_running)
            {
                break;
            }
            bool force = false;
            if (_resizePending)
            {
                _resizePending = false;
                var size = _pendingSize;
                _swapChain!.ResizeBuffers(BufferCount, (uint)size.W, (uint)size.H, BufferFormat, SwapChainFlags.None).CheckError();
                _size = size;
                force = true;
            }
            ulong flags = LibMpv.RenderContextUpdate(_renderContext);
            if ((flags & LibMpv.RenderUpdateFrame) != 0 || force)
            {
                RenderFrame();
            }
        }
    }

    private void RenderFrame()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        nint fbo = Marshal.AllocHGlobal(sizeof(nint));
        try
        {
            Marshal.WriteIntPtr(fbo, backBuffer.NativePointer);
            LibMpv.MpvRenderParam[] parameters =
            [
                new() { Type = LibMpv.RenderParamD3D11Fbo, Data = fbo },
                new() { Type = LibMpv.RenderParamInvalid, Data = 0 },
            ];
            int rc = LibMpv.RenderContextRender(_renderContext, parameters);
            if (rc < 0)
            {
                SpikeLog.Write("surface", $"render failed: {LibMpv.Error(rc)}");
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fbo);
        }
        _swapChain.Present(1, PresentFlags.None);
        LibMpv.RenderContextReportSwap(_renderContext);
        Interlocked.Increment(ref _framesRendered);
    }

    public void UpdateLayout()
    {
        if (_swapChain is null)
        {
            return;
        }
        var size = SwapChainScale.PixelSize(panel);
        if (size == _size)
        {
            return;
        }
        _pendingSize = size;
        _resizePending = true;
        SwapChainScale.ApplyInverseScale(_swapChain.NativePointer, panel);
        _wake.Set();
        SpikeLog.Write("surface", $"render-api resize {size.Width}x{size.Height}");
    }

    public string Describe() => $"render-api {_size.W}x{_size.H} frames={Interlocked.Read(ref _framesRendered)}";

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _renderThread?.Join(2000);
        if (_renderContext != 0)
        {
            // Must happen before mpv_terminate_destroy.
            LibMpv.RenderContextFree(_renderContext);
            _renderContext = 0;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
        _swapChain?.Dispose();
        _device?.Dispose();
    }
}
