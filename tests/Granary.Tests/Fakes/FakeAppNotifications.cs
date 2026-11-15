using Granary.Core.Abstractions;
using Granary.Core.Downloads;

namespace Granary.Tests.Fakes;

/// <summary>Windows' notifications as the test sets them: whether Granary is in front, whether Windows takes one, and what it took.</summary>
internal sealed class FakeAppNotifications : IAppNotifications
{
    public bool IsAppInFront { get; set; } = true;

    /// <summary>Windows takes notifications from Granary.</summary>
    public bool Takes { get; set; } = true;

    public List<DownloadAnnouncement> Shown { get; } = [];

    public bool TryShow(DownloadAnnouncement announcement)
    {
        if (!Takes)
        {
            return false;
        }
        Shown.Add(announcement);
        return true;
    }
}
