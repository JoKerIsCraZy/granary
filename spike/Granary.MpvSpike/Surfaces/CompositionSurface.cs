using Granary.MpvSpike.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Granary.MpvSpike.Surfaces;

/// <summary>
/// Variant C: mpv's own d3d11 renderer in "composition" mode creates a composition swap chain
/// (read from the display-swapchain property) that the app sets on a XAML SwapChainPanel.
/// </summary>
internal sealed class CompositionSurface(SwapChainPanel panel, DispatcherQueue dispatcher, bool hdrOutput) : IVideoSurface
{
    private MpvPlayer? _player;
    private nint _attachedSwapChain;
    private DispatcherQueueTimer? _pollTimer;
    private (int Width, int Height) _size;

    public string Name => "composition";

    public void ConfigureBeforeInit(MpvPlayer player)
    {
        _player = player;
        _size = SwapChainScale.PixelSize(panel);
        if (_size.Width < 16 || _size.Height < 16)
        {
            _size = (1280, 720);
        }
        player.SetOption("vo", "gpu-next");
        player.SetOption("gpu-api", "d3d11");
        player.SetOption("gpu-context", "d3d11");
        player.SetOption("d3d11-output-mode", "composition");
        player.SetOption("d3d11-composition-size", $"{_size.Width}x{_size.Height}");
        player.SetOption("force-window", "yes");
        if (hdrOutput)
        {
            // Composition mode cannot query the monitor, so the app states the target itself.
            player.SetOption("d3d11-output-format", "rgb10_a2");
            player.SetOption("d3d11-output-csp", "pq");
            player.SetOption("target-trc", "pq");
            player.SetOption("target-prim", "bt.2020");
        }
    }

    public void AttachAfterInit(MpvPlayer player)
    {
        // The swap chain exists once the VO has initialized; poll briefly for it.
        _pollTimer = dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(50);
        _pollTimer.Tick += (_, _) => TryAttach();
        _pollTimer.Start();
    }

    private void TryAttach()
    {
        long? pointer = _player?.GetInt64("display-swapchain");
        if (pointer is null or 0)
        {
            return;
        }
        nint swapChain = (nint)pointer.Value;
        if (swapChain != _attachedSwapChain)
        {
            Native.SetSwapChain(panel, swapChain);
            _attachedSwapChain = swapChain;
            SpikeLog.Write("surface", $"composition swap chain attached (size {_size.Width}x{_size.Height})");
        }
        SwapChainScale.ApplyInverseScale(swapChain, panel);
    }

    public void UpdateLayout()
    {
        if (_player is null)
        {
            return;
        }
        var size = SwapChainScale.PixelSize(panel);
        if (size == _size)
        {
            return;
        }
        _size = size;
        int rc = _player.SetProperty("d3d11-composition-size", $"{size.Width}x{size.Height}");
        SpikeLog.Write("surface", $"composition resize {size.Width}x{size.Height} rc={rc}");
        TryAttach();
    }

    public string Describe() => $"composition {_size.Width}x{_size.Height}";

    public void Dispose()
    {
        _pollTimer?.Stop();
        _player = null;
    }
}
