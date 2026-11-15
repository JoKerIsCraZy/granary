using Granary.Core.Texts;
using Granary.Core.ViewModels;

namespace Granary.App.Views;

// The player's words that x:Bind cannot take from a key alone: the menus' headings, which the web writes in mixed case
// and shows in capitals, and the names of the delay buttons, which hold their step.
public sealed partial class PlayerPage
{
    public string AudioHeading => AppText.Upper(AppText.Get("web.player.audioMenu.heading"));

    public string DelayHeading => AppText.Upper(AppText.Get("web.subtitles.menu.delay"));

    public string TimingHeading => AppText.Upper(AppText.Get("web.subtitles.menu.timing"));

    public string ChaptersHeading => AppText.Upper(AppText.Get("web.player.chaptersMenu.heading"));

    public string AudioDelayHeading => AppText.Upper(AppText.Get("granary.player.audioDelay"));

    public string VersionHeading => AppText.Upper(AppText.Get("web.player.quality.version"));

    public string QualityHeading => AppText.Upper(AppText.Get("web.player.quality.title"));

    public string PartyHeading => AppText.Upper(AppText.Get("web.player.watchTogether.title"));

    public string OnDeckHeading => AppText.Upper(AppText.Get("upNext.onDeck"));

    /// <summary>"Subtitle delay 100ms earlier", as the web's SubtitleMenu names its step buttons.</summary>
    public string SubtitleEarlierName => AppText.Get("web.subtitles.menu.delayEarlier", ("step", SubtitleDelayStepMs));

    public string SubtitleLaterName => AppText.Get("web.subtitles.menu.delayLater", ("step", SubtitleDelayStepMs));

    public string AudioEarlierName => AppText.Get("granary.player.audioDelayEarlier", ("step", PlayerViewModel.AudioDelayStepMs));

    public string AudioLaterName => AppText.Get("granary.player.audioDelayLater", ("step", PlayerViewModel.AudioDelayStepMs));

    /// <summary>Play or Pause on the minimized player's bar, in the words of the web's player bar.</summary>
    public string DockPlayPauseName(bool isPaused) => isPaused ? AppText.Get("web.player.bar.play") : AppText.Get("web.player.bar.pause");
}
