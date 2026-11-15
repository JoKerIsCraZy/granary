using System.Globalization;

namespace Granary.Core.Remote;

/// <summary>
/// What the phone remote writes to the log. A line names a connection by its number and says what happened in one of the
/// fixed phrases below; it never carries a value from a frame, a key, a nonce, a name, an address or an id. An
/// exception is logged by its type and code only: a socket's or a file's message can name an address or a path. The App's
/// listener, advertiser and address source log the same way.
/// </summary>
public static class RemoteLog
{
    /// <summary>An exception as the log may name it: its type and code.</summary>
    public static string Failure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
    }
}

/// <summary>
/// Why a phone's connection ended or was refused, or why the receiver stopped, as the log says it: every reason the log
/// names is one of these.
/// </summary>
public static class RemoteLogReasons
{
    public const string NotLocal = "not a local address";
    public const string TooManyNewConnections = "too many new connections from one address";
    public const string NotAPhonesHello = "the hello was not a phone's";
    public const string Replaced = "a newer connection of the same phone";
    public const string DisconnectedHere = "disconnected on this PC";
    public const string SwitchedOff = "switched off";
    public const string NoProfile = "no profile signed in";
    public const string NoLocalNetwork = "no local network";
    public const string AnotherServer = "another server";
    public const string NoPortAtNewAddress = "no port at the new address";
    public const string ListenerFailed = "the listener stopped taking connections";
    public const string AdvertisementFailed = "the advertisement failed";
    public const string NotAPairingHello = "the first frame was not a pairing hello";
    public const string WrongKey = "wrong pairing key";
    public const string FirstFrameTooLarge = "the first frame was too large";
    public const string NoPairingHello = "no pairing hello in time";
    public const string NoHello = "no hello in time";
    public const string FrameTooLarge = "a frame was too large";
    public const string TooManyFrames = "too many frames";
    public const string FrameDidNotOpen = "a frame did not open";
    public const string Malformed = "a message could not be read";
    public const string PhoneClosed = "the phone closed";
    public const string PhoneLeft = "the phone went away";
    public const string NotReading = "the phone stopped reading";
    public const string NoPongs = "no answer to pings";
    public const string KeyReset = "the pairing code was reset";
    public const string Stopped = "the receiver stopped";
    public const string MessageFailed = "a message could not be handled";
    public const string MadeRoom = "made room for a new connection";
    public const string MadeRoomForPhone = "made room for another phone";
    public const string TooManyPhones = "too many phones";
}
