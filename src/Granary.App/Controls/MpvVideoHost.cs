using Granary.App.Interop;
using Granary.Player;
using Granary.Player.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// Shows mpv's composition swap chain. XAML placed above this control draws over the video,
/// which is why Granary uses composition mode instead of a child window (spike/README.md).
/// </summary>
public sealed partial class MpvVideoHost : UserControl
{
    private readonly SwapChainPanel _panel = new();
    private ICompositionVideoOutput? _output;
    private nint _attached;

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
        int width = (int)Math.Round(_panel.ActualWidth * _panel.CompositionScaleX);
        int height = (int)Math.Round(_panel.ActualHeight * _panel.CompositionScaleY);
        _output.Resize(width, height);
        if (_attached != 0)
        {
            DxgiSwapChain.SetInverseScale(_attached, _panel.CompositionScaleX, _panel.CompositionScaleY);
        }
    }
}
