namespace Granary.Core.Remote;

/// <summary>
/// Whether Windows is locked: a phone may not start a title then, though it may still control what plays (the owner's
/// decision, design 5.10). The App follows the session's lock and unlock notices; read from any thread.
/// </summary>
public interface IPcLock
{
    /// <summary>The PC is locked now; false until Windows says otherwise.</summary>
    bool IsLocked { get; }
}

/// <summary>Core's stand-in until the App puts in its own: never locked.</summary>
public sealed class NoPcLock : IPcLock
{
    public bool IsLocked => false;
}
