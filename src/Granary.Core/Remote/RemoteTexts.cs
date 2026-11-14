using System.Globalization;
using Granary.Core.Servers;

namespace Granary.Core.Remote;

/// <summary>
/// The texts Granary sends to a phone, which the phone shows as they are. They follow Silo's Roku app's, with "Roku" as
/// "PC" (docs/api-contract.md 15.5 lists them). Silo's receivers write them in English.
/// </summary>
public static class RemoteTexts
{
    /// <summary>The longest name of a phone Granary shows.</summary>
    public const int MaxNameLength = 64;

    /// <summary><c>error unauthorized</c>: silo-roku's <c>receiver.bs</c> <c>frame</c>.</summary>
    public const string Unauthorized = "Connect with a matching Silo account first.";

    /// <summary><c>error</c> and <c>handoff_cancel</c> <c>version_unsupported</c>: <c>receiver.bs</c> <c>hello</c>.</summary>
    public const string VersionUnsupported = "Update Silo on both devices to continue.";

    /// <summary><c>error player_not_ready</c>: <c>AppSceneControl.bs</c> <c>controlApply</c>.</summary>
    public const string PlayerNotReady = "The PC player is not ready yet.";

    /// <summary><c>error handoff_required</c>: <c>AppSceneControl.bs</c>, a launch without a ready offer.</summary>
    public const string HandoffRequired = "Prepare the phone profile before playing.";

    /// <summary><c>error server_mismatch</c>: <c>AppSceneControl.bs</c>, a launch for another server.</summary>
    public const string ServerMismatch = "This PC is connected to a different Silo server.";

    /// <summary><c>error launch_pending</c>: <c>AppSceneControl.bs</c>, a second launch while one opens.</summary>
    public const string LaunchPending = "A title is already starting on this PC.";

    /// <summary><c>error too_many_controllers</c>: <c>receiver.bs</c> <c>accepted</c>.</summary>
    public const string TooManyControllers = "Too many devices are controlling this PC. Disconnect one and try again.";

    /// <summary><c>error rate_limited</c>: <c>receiver.bs</c> <c>frame</c>.</summary>
    public const string RateLimited = "Too many commands. Slow down and try again.";

    /// <summary><c>error command_failed</c> for <c>play_next</c> without one: <c>PlayerRemote.bs</c>.</summary>
    public const string NoNextEpisode = "There is no next episode.";

    /// <summary>
    /// <c>error command_failed</c> for play, pause or seek that a Watch Party's rules refuse: the Roku app's
    /// <c>watchParty.player.notAllowed</c>.
    /// </summary>
    public const string NotAllowedInWatchParty = "This Watch Party does not allow this playback action.";

    /// <summary><c>error command_failed</c> for any other command the player refused: <c>PlayerRemote.bs</c>'s "Not available on Roku.".</summary>
    public const string NotAvailable = "Not available on this PC.";

    /// <summary>
    /// <c>handoff_cancel handoff_failed</c> for an offer on Granary's server until a phone can start titles here (Part 2
    /// of the phone remote replaces it). New: no Silo text says this.
    /// </summary>
    public const string StartingNotYetAvailable = "This PC can't start titles from a phone yet.";

    /// <summary>A phone that gave no usable name. New.</summary>
    public const string UnnamedPhone = "A phone";

    /// <summary><c>handoff_cancel handoff_failed</c> for an offer from another server: <c>AppSceneControl.bs</c> <c>controlOffer</c>.</summary>
    public static string PlaysFrom(string serverName) => string.Format(CultureInfo.InvariantCulture, "This PC plays from {0}.", serverName);

    /// <summary>
    /// A name a phone sent, as Granary shows it: cleaned as a discovered server's name is (no control, format or separator
    /// characters, compatibility forms folded), at most <see cref="MaxNameLength"/> characters.
    /// </summary>
    public static string CleanName(string? text) => LanAdverts.CleanLabel(text, MaxNameLength) ?? UnnamedPhone;
}
