namespace Granary.MpvSpike.Surfaces;

/// <summary>One way of getting mpv's video onto the WinUI 3 window.</summary>
internal interface IVideoSurface : IDisposable
{
    string Name { get; }

    /// <summary>Sets the vo/gpu options before mpv_initialize.</summary>
    void ConfigureBeforeInit(MpvPlayer player);

    /// <summary>Hooks the surface up after mpv_initialize (render context, swap chain, window).</summary>
    void AttachAfterInit(MpvPlayer player);

    /// <summary>Called on the UI thread whenever the video area changes size or position.</summary>
    void UpdateLayout();

    /// <summary>A short status line for the overlay and the log.</summary>
    string Describe();
}
