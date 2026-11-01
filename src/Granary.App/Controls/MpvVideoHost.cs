using System.Runtime.InteropServices;
using Granary.App.Interop;
using Granary.Player;
using Granary.Player.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

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
    private PixelSize _panelSize;
    private PixelSize? _pendingResize;
    private int _resizeTurn;
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

    // The hide joins the very frame that first lays the panel out at its new size: SizeChanged can come after that
    // frame has been drawn, which would show the old picture in the new place for a frame.
    protected override Size ArrangeOverride(Size finalSize)
    {
        NotePanelSize(PhysicalSize(finalSize.Width, finalSize.Height));
        return base.ArrangeOverride(finalSize);
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
        _resizeGate.Reset();
        _pendingResize = null;
        _resizeTurn++;
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

    private PixelSize PhysicalSize(double width, double height) => new(
        (int)Math.Round(width * _panel.CompositionScaleX),
        (int)Math.Round(height * _panel.CompositionScaleY));

    // A new size, from the layout pass or SizeChanged, whichever comes first: a jump hides the picture.
    private void NotePanelSize(PixelSize size)
    {
        if (size.IsEmpty || size == _panelSize)
        {
            return;
        }
        _panelSize = size;
        // Only a connected, loaded panel shows the picture; one that left the page passes the size on as before.
        if (_output is null || !IsLoaded)
        {
            return;
        }
        _resizeGate.PanelResized(size, DxgiSwapChain.BufferSize(_attached));
        if (_resizeGate.IsHidden)
        {
            _panel.Opacity = 0;
            WatchFrames();
        }
    }

    private void ReportSize()
    {
        if (_output is null || _panel.ActualWidth < 1 || _panel.ActualHeight < 1)
        {
            return;
        }
        var size = PhysicalSize(_panel.ActualWidth, _panel.ActualHeight);
        NotePanelSize(size);
        if (_resizeGate.IsHidden)
        {
            // mpv gets the new size only once the hidden panel is on the screen (ResizeAfterTheHideShows).
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
            ResizeAfterTheHideShows(size, ++_resizeTurn);
            return;
        }
        if (_resizeGate.Frame(DxgiSwapChain.BufferSize(_attached), DxgiSwapChain.PresentCount(_attached)))
        {
            StopWatchingFrames();
            _panel.Opacity = 1;
        }
    }

    // The hidden panel and mpv's pictures reach the desktop compositor separately: a heavy frame (the whole app laid out
    // again as the player minimizes) can arrive after mpv's first picture at the new size, which then shows in the old
    // place. This frame's commit has gone out; once the compositor has drawn twice more, it is on the screen, and only
    // then does mpv get the new size. A newer size or a disconnect in the meantime takes the turn.
    private void ResizeAfterTheHideShows(PixelSize size, int turn)
    {
        var queue = DispatcherQueue;
        _ = Task.Run(() =>
        {
            _ = DwmFlush();
            _ = DwmFlush();
            queue.TryEnqueue(() =>
            {
                if (turn == _resizeTurn)
                {
                    _output?.Resize(size.Width, size.Height);
                }
            });
        });
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();
}
