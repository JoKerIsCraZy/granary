namespace Granary.Core.Auth;

/// <summary>
/// A call meant for one server found another one active: it was not applied. The session has moved on, and whoever
/// asked shows the new server's state rather than an error.
/// </summary>
public sealed class ActiveServerChangedException : InvalidOperationException
{
    public ActiveServerChangedException()
        : base("Another server became active.")
    {
    }

    public ActiveServerChangedException(string message)
        : base(message)
    {
    }

    public ActiveServerChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
