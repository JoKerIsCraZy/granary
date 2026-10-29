using Granary.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// One of the two layers a backdrop crossfades between (<see cref="BrowseHero"/>, <see cref="FocusBackdrop"/>): its blur,
/// its picture, and what it is loading.
/// </summary>
internal sealed class BackdropLayer(Grid grid, Image blur, Image art)
{
    private CancellationTokenSource? _loading;

    public Grid Grid { get; } = grid;

    public BrowseHeroTitle? Title { get; private set; }

    /// <summary>The picture waits: the width was not known yet, or the hero was folded.</summary>
    public bool WaitsForArt { get; private set; }

    public int DecodedWidth { get; private set; }

    public void Fill(BrowseHeroTitle title, int width, bool folded)
    {
        Clear();
        Title = title;
        blur.Source = Thumbhashes.Get(title.BackdropThumbhash);
        WaitsForArt = title.BackdropUrl is not null;
        if (!folded)
        {
            LoadArt(width);
        }
    }

    public void LoadArt(int width)
    {
        if (Title?.BackdropUrl is not { } url || width <= 0)
        {
            return;
        }
        Replace(ref _loading);
        art.Opacity = 0;
        art.Source = Artwork.Load(url, width, _loading!.Token);
        DecodedWidth = width;
        WaitsForArt = false;
    }

    public void Clear()
    {
        Replace(ref _loading, renew: false);
        blur.Source = null;
        art.Source = null;
        art.Opacity = 0;
        Title = null;
        WaitsForArt = false;
        DecodedWidth = 0;
    }

    /// <summary>Cancels and lets go of <paramref name="loading"/>, and starts a new one unless <paramref name="renew"/> is false.</summary>
    internal static void Replace(ref CancellationTokenSource? loading, bool renew = true)
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = renew ? new CancellationTokenSource() : null;
    }
}
