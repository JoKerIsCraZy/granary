namespace Granary.Player;

public enum VideoOutputKind
{
    /// <summary>mpv's d3d11 renderer in composition mode, shown on a XAML SwapChainPanel.</summary>
    D3D11Composition,

    /// <summary>No video or audio output (tests).</summary>
    None,
}

public sealed record MpvEngineOptions
{
    public VideoOutputKind VideoOutput { get; init; } = VideoOutputKind.D3D11Composition;

    /// <summary>mpv <c>hwdec</c>; <c>auto-safe</c> picks d3d11va on Windows when the GPU supports the codec.</summary>
    public string HardwareDecoding { get; init; } = "auto-safe";

    /// <summary>Initial swap-chain size until the host reports its real size.</summary>
    public (int Width, int Height) InitialOutputSize { get; init; } = (1280, 720);
}
