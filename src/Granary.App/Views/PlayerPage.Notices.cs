namespace Granary.App.Views;

// Notices from the server or an administrator (PlaybackNoticeView): over the full player, or above the minimized bar,
// where the window shows them. Only the mini view, too small for one, keeps a notice until the full player shows again.
public sealed partial class PlayerPage
{
    private void InitializeNotices() =>
        RegisterPropertyChangedCallback(IsCompactProperty, (_, _) => ViewModel.HoldNotices(IsMiniView));
}
