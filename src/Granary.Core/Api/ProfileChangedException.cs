namespace Granary.Core.Api;

/// <summary>
/// A write made for one profile was about to go out while its server acts as another profile, or as none: it was not
/// sent, so it cannot change the other profile's data (<see cref="SiloRequestOptions.Profile"/>).
/// </summary>
public sealed class ProfileChangedException : Exception
{
    public ProfileChangedException()
        : base("The request was made for another profile than the one the server now acts as; it was not sent.")
    {
    }

    public ProfileChangedException(string message)
        : base(message)
    {
    }

    public ProfileChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
