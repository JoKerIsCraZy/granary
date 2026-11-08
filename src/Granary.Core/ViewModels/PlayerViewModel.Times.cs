using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Granary.Core.ViewModels;

// The times under the title, "31:55 / 48:41" as the web player writes them, whose end a click turns into the time left.
// Granary's own: the web forgets that choice with the page, this PC keeps it for the next player
// (IPlaybackDevicePreferences.TimeLeft).
public sealed partial class PlayerViewModel
{
    /// <summary>The times end with the time left ("-16:46") instead of the duration, as the viewer last chose on this PC.</summary>
    [ObservableProperty]
    public partial bool ShowsTimeLeft { get; private set; }

    /// <summary>A click on the times: their end switches between the duration and the time left, kept for this PC.</summary>
    [RelayCommand]
    private void ToggleTimeLeft()
    {
        ShowsTimeLeft = !ShowsTimeLeft;
        // Never fails: a save that does not work is logged, and the choice holds for this player.
        _ = _devicePreferences?.SetTimeLeftAsync(ShowsTimeLeft);
    }
}
