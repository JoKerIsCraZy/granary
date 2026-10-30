using Granary.Core.ViewModels.Detail;

namespace Granary.App;

// A trailer over the window (Controls/TrailerOverlay): Back closes it before it goes anywhere, any other page or the player
// closes it, and Granary's own player, minimized to its bar, pauses for it unless it plays a Watch Party's title.
public sealed partial class MainWindow
{
    /// <summary>The trailer shown over the window, if any.</summary>
    public TrailerOverlayViewModel Trailers { get; }

    private void InitializeTrailers()
    {
        Trailers.Opened += OnTrailerOpened;
        // Under a trailer that fills the window the app is hidden, as under the full player: laid out at the screen's size,
        // the page under it lost its scroll position.
        TrailerLayer.FullScreenChanged += (_, fullScreen) => ShowApp(!fullScreen);
        // A trailer still open as the window closes lets its browser go with it.
        Closed += (_, _) => Trailers.Close();
    }

    private void OnTrailerOpened(object? sender, EventArgs e)
    {
        if (_player is { IsDocked: true } bar)
        {
            bar.ViewModel.PauseForTrailer();
        }
    }

    // Back (Alt+Left, the mouse's back button) while a trailer is open closes it, and goes no further.
    private bool CloseTrailer()
    {
        if (!Trailers.IsOpen)
        {
            return false;
        }
        Trailers.Close();
        return true;
    }
}
