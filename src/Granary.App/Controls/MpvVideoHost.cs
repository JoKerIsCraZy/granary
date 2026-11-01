using Granary.App.Interop;
using Granary.Player;
using Granary.Player.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>
/// Shows mpv's composition swap chain. XAML placed above this control draws over the video,
/// which is why Granary uses composition mode instead of a child window (spike/README.md).
/// After a jump in size (the player minimized to its bar or brought back, full screen) the picture hides until mpv has
/// presented at the new size, so the panel never shows the old picture's corner (<see cref="CompositionResizeGate"/>).
/// </summary>
public sealed partial class MpvVideoHost : UserControl
{
    private readonly SwapChainPanel _panel = new();
    private readonly CompositionResizeGate _resizeGate = new(TimeProvider.System);
    private ICompositionVideoOutput? _output;
    private nint _attached;
    private PixelSize? _pendingResize;
    private bool _isWatchingFrames;

    public MpvVideoHost()
    {
        Content = _panel;
        _panel.SizeChanged += (_, _) => ReportSize();
        _panel.CompositionScaleChanged += (_, _) => ReportSize();
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => Disconnect();
    }

    /// <summary>The engine's video output. Set by the page before the control loads.</summary>
    public ICompositionVideoOutput? VideoOutput
    {
        get => _output;
        set
        {
            Disconnect();
            _output = value;
            if (IsLoaded)
            {
                Connect();
            }
        }
    }

    private void Connect()
    {
        if (_output is null)
        {
            return;
        }
        _output.SwapChainChanged += OnSwapChainChanged;
        ReportSize();
        Attach();
    }

    private void Disconnect()
    {
        StopWatchingFrames();
        _pendingResize = null;
        _panel.Opacity = 1;
        if (_output is not null)
        {
            _output.SwapChainChanged -= OnSwapChainChanged;
        }
        if (_attached != 0)
        {
            SwapChainPanelNative.SetSwapChain(_panel, 0);
            _attached = 0;
        }
    }

    private void OnSwapChainChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Attach);

    private void Attach()
    {
        nint swapChain = _output?.SwapChain ?? 0;
        if (swapChain == 0)
        {
            return;
        }
        if (swapChain != _attached)
        {
            SwapChainPanelNative.SetSwapChain(_panel, swapChain);
            _attached = swapChain;
        }
        DxgiSwapChain.SetInverseScale(swapChain, _panel.CompositionScaleX, _panel.CompositionScaleY);
    }

    private void ReportSize()
    {
        if (_output is null || _panel.ActualWidth < 1 || _panel.ActualHeight < 1)
        {
            return;
        }
        var size = new PixelSize(
            (int)Math.Round(_panel.ActualWidth * _panel.CompositionScaleX),
            (int)Math.Round(_panel.ActualHeight * _panel.CompositionScaleY));
        _resizeGate.PanelResized(size, DxgiSwapChain.BufferSize(_attached));
        if (_resizeGate.IsHidden)
        {
            // The hidden picture reaches the screen with this frame; mpv gets the new size on the next one, so its
            // first picture at that size cannot land in the panel before the panel is hidden.
            _panel.Opacity = 0;
            _pendingResize = size;
            WatchFrames();
        }
        else
        {
            _output.Resize(size.Width, size.Height);
        }
        if (_attached != 0)
        {
            DxgiSwapChain.SetInverseScale(_attached, _panel.CompositionScaleX, _panel.CompositionScaleY);
        }
    }

    private void WatchFrames()
    {
        if (!_isWatchingFrames)
        {
            _isWatchingFrames = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void StopWatchingFrames()
    {
        if (_isWatchingFrames)
        {
            _isWatchingFrames = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, object e)
    {
        if (_pendingResize is { } size)
        {
            _pendingResize = null;
            _output?.Resize(size.Width, size.Height);
            return;
        }
        if (_resizeGate.Frame(DxgiSwapChain.BufferSize(_attached), DxgiSwapChain.PresentCount(_attached)))
        {
            StopWatchingFrames();
            _panel.Opacity = 1;
        }
    }
}
