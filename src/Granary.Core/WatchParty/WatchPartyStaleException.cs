namespace Granary.Core.WatchParty;

/// <summary>
/// A create or join whose answer came after the room was left or the profile or server changed: nothing was entered,
/// and there is nothing to tell the viewer.
/// </summary>
public sealed class WatchPartyStaleException : OperationCanceledException
{
    public WatchPartyStaleException()
        : base("The room was left, or the profile or server changed, while the room was asked for.")
    {
    }

    public WatchPartyStaleException(string message)
        : base(message)
    {
    }

    public WatchPartyStaleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
