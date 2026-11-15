using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

/// <summary>
/// Granary hears from the active server again after it could not reach it: the Downloads page's Retry found it
/// (<c>OfflineStart</c>), the events connection subscribed again after an attempt that failed (<c>RealtimeEvents</c>), or what
/// the server lets the profile download was read after a read that failed (<c>DownloadsFeature</c>). Whoever waits for the
/// server, as the positions recorded offline do (<c>OfflineProgressSync</c>), goes on at once rather than with its next retry.
/// One for the app; each of those says so once per recovery.
/// </summary>
public sealed class ServerRecovery(ILogger<ServerRecovery> logger)
{
    /// <summary>Raised on the reporter's thread, any thread.</summary>
    public event EventHandler? Recovered;

    /// <summary>
    /// The server answered again; <paramref name="how"/> names how, for the log: "Retry", "the events connection". Each
    /// listener hears it on its own: one that fails is logged by its kind, and neither the reporter (a read, a frame of the
    /// events connection) nor the other listeners feel it.
    /// </summary>
    public void Report(string how)
    {
        logger.LogInformation("The server answers again ({How})", how);
        if (Recovered is not { } recovered)
        {
            return;
        }
        foreach (var listener in recovered.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                listener(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                logger.LogError("A listener of the server's recovery failed ({Failure})", ex.GetType().Name);
            }
        }
    }
}
