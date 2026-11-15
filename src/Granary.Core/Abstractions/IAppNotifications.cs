using Granary.Core.Downloads;

namespace Granary.Core.Abstractions;

/// <summary>
/// Windows' notifications for Granary, and whether Granary's window is in front, which decides whether a download's message
/// shows in the window or as a notification (<c>DownloadNotices</c>). The app's goes through the Windows App SDK; until it
/// registers its own, <see cref="NoAppNotifications"/> keeps every message in the window.
/// </summary>
public interface IAppNotifications
{
    /// <summary>Granary's window is the one in front: not minimized, and not behind another app's window.</summary>
    bool IsAppInFront { get; }

    /// <summary>
    /// Shows a download's message as a Windows notification, with Play when something finished and Open Downloads; false when
    /// Windows takes none from Granary (not registered, turned off in Windows' settings, or it failed).
    /// </summary>
    bool TryShow(DownloadAnnouncement announcement);
}

/// <summary>No Windows notifications: Granary counts as in front, so every message shows in the window.</summary>
public sealed class NoAppNotifications : IAppNotifications
{
    public bool IsAppInFront => true;

    public bool TryShow(DownloadAnnouncement announcement) => false;
}
