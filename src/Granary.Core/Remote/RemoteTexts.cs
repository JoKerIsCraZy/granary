using System.Globalization;
using Granary.Core.Playback;
using Granary.Core.Servers;
using Granary.Core.Texts;

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
    /// <c>handoff_cancel handoff_failed</c> for an offer for another profile than the one signed in on the PC: Granary
    /// plays only for its own (design 5.8). New: Silo's Roku app says "This Roku can play only for the profile open on it".
    /// </summary>
    public const string AnotherProfile = "Granary is signed in with another profile. Switch profiles on the PC to play from this phone.";

    /// <summary><c>handoff_cancel</c> and <c>error</c> <c>watch_party_active</c>: the PC is in a Watch Party's room (design 5.8). New.</summary>
    public const string LeaveWatchParty = "Leave the Watch Party on the PC first.";

    /// <summary>
    /// <c>error launch_failed</c> for a launch whose content id can name no title (blank, "." or ".."): the player's own words
    /// for a title the server does not have (<c>PlaybackStarter.Describe</c>).
    /// </summary>
    public const string NoSuchTitle = "This title is no longer available.";

    /// <summary>
    /// The player's failure as a phone is told it, in the status and with <c>launch_failed</c>, when the player's text is
    /// not one of Granary's own: a server's or an administrator's message comes as they wrote it. New.
    /// </summary>
    public const string CouldNotPlay = "This title could not be played on this PC.";

    // The player's own failure texts that it words in the language on screen, which name nothing (ServerPlayback,
    // ServerPlayback.Recovery, PlaybackStarter, PlayerViewModel, ApiFailureText). A text added there later and not here goes
    // to a phone as CouldNotPlay.
    private static readonly string[] PlayerFailureKeys =
    [
        "granary.player.error.titleUnavailable",
        "granary.player.error.notPlayableNow",
        "granary.player.error.notPlayable",
        "granary.player.error.protocolUnsupported",
        "granary.player.error.untrustedAddress",
        "granary.player.error.connectionLost",
        "granary.player.error.unexpected",
        "granary.player.error.serverEnded",
        "granary.player.error.adminStopped",
        "granary.player.error.credentialsRejected",
        "granary.player.error.signInRejected",
        "granary.player.error.networkPath",
        "web.player.session.endedByAdmin",
        "granary.apiFailure.busy",
        "granary.apiFailure.unreachable",
        "granary.apiFailure.unexpected",
        "granary.apiFailure.tooManyRequestsLater",
    ];

    // The ones still worded in English in code (MediaAuthorization, PlaybackStarter).
    private static readonly HashSet<string> EnglishPlayerFailures = new(StringComparer.Ordinal)
    {
        "Sign in to play from this server.",
        "Choose a profile to play from this server.",
        "Choose a server to play from.",
        "The server ended the session. Sign in again to play.",
        "The server could not renew the sign-in. Try again in a moment.",
        "This title has no file to play.",
        "The room's file is not available here.",
        "This server does not offer playback to this app.",
        PlaybackUnavailableException.StartedForAnotherProfile,
    };

    /// <summary>A phone that gave no usable name. New.</summary>
    public const string UnnamedPhone = "A phone";

    /// <summary><c>handoff_cancel handoff_failed</c> for an offer from another server: <c>AppSceneControl.bs</c> <c>controlOffer</c>.</summary>
    public static string PlaysFrom(string serverName) => string.Format(CultureInfo.InvariantCulture, "This PC plays from {0}.", serverName);

    // What the web's "Playback error: {message}" holds when the engine ends a title by itself: mpv's own error texts, as
    // mpv_error_string gives them for an end-file error (LibMpv.Error; checked against the pinned libmpv-2.dll), and
    // Granary's details (MpvPlaybackEngine, ServerPlayback). Nothing else goes in it, so no server's words can.
    private static readonly string[] PlaybackEndDetails =
    [
        "event queue full",
        "memory allocation failed",
        "core not initialized",
        "invalid parameter",
        "option not found",
        "unsupported format for accessing option",
        "error setting option",
        "property not found",
        "unsupported format for accessing property",
        "property unavailable",
        "error accessing property",
        "error running command",
        "loading failed",
        "audio output initialization failed",
        "video output initialization failed",
        "no audio or video data played",
        "unrecognized file format",
        "not supported",
        "operation not implemented",
        "something happened",
        "unknown error",
        "Granary could not open the title",
        "Granary could not start the player without your mpv.conf",
        "Granary stopped using your mpv.conf",
        "No picture within the start timeout.",
        "No data within the stall timeout.",
    ];

    private const string PlaybackErrorKey = "web.player.error.playbackError";
    private const string StartFailedKey = "granary.player.error.startFailed";
    private const string TooManyRequestsKey = "granary.apiFailure.tooManyRequests";

    // The largest number a numbered text is checked for; more digits are no status and no wait in minutes.
    private const long MaxNumber = 100_000_000;

    // Longer than any of the player's own texts in any language, numbers included: a longer text is not one of them, and
    // is not searched, so a server's long message costs nothing.
    private const int MaxOwnFailureLength = 1000;

    // A numbered text holds one number, which a grouped one makes three candidates at most ("1,234": 1, 234 and 1234).
    private const int MaxNumberCandidates = 4;

    /// <summary>
    /// The player's failure as a phone may read it: when it is one of Granary's own, which name nothing, the same text in
    /// English, else <see cref="CouldNotPlay"/>. A terminal decision's message and an administrator's stop message are the
    /// server's words and could name a host or an address, as could any text that came from elsewhere.
    /// </summary>
    public static string? PlayerError(string? message) =>
        message is null ? null : GranarysOwnFailureInEnglish(message) ?? CouldNotPlay;

    // The player's texts are compared in the language on screen, as it worded them; a numbered one by the numbers in it.
    // Text that is sent stays English, as Silo's rule is and as Silo's receivers write theirs.
    private static string? GranarysOwnFailureInEnglish(string message)
    {
        if (message.Length > MaxOwnFailureLength)
        {
            return null;
        }
        if (EnglishPlayerFailures.Contains(message))
        {
            return message;
        }
        foreach (string key in PlayerFailureKeys)
        {
            if (AppText.Get(key) == message)
            {
                return AppText.English.Get(key);
            }
        }
        foreach (string detail in PlaybackEndDetails)
        {
            if (AppText.Get(PlaybackErrorKey, ("message", detail)) == message)
            {
                return AppText.English.Get(PlaybackErrorKey, [("message", detail)]);
            }
        }
        var numbers = NumbersIn(message);
        if (numbers.Count > MaxNumberCandidates)
        {
            return null;
        }
        foreach (long number in numbers)
        {
            if (AppText.Get(StartFailedKey, ("status", number)) == message)
            {
                return AppText.English.Get(StartFailedKey, [("status", number)]);
            }
            if (AppText.Get(TooManyRequestsKey, ("count", number)) == message)
            {
                return AppText.English.Get(TooManyRequestsKey, [("count", number)]);
            }
        }
        return null;
    }

    // The whole numbers a text holds, in any script's digits, also one a language groups ("1,234", "1.234", "1 234"):
    // the candidates for a numbered text, which matches only when its number was one of them.
    private static HashSet<long> NumbersIn(string text)
    {
        var numbers = new HashSet<long>();
        long? run = null;
        long? grouped = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsDigit(c))
            {
                int digit = (int)char.GetNumericValue(c);
                run = Append(run, digit);
                grouped = Append(grouped, digit);
                continue;
            }
            Add(numbers, run);
            run = null;
            // A separator between digits keeps a grouped number going; anything else ends it.
            bool separates = grouped is not null && !char.IsLetter(c) && i + 1 < text.Length && char.IsDigit(text[i + 1]);
            if (!separates)
            {
                Add(numbers, grouped);
                grouped = null;
            }
        }
        Add(numbers, run);
        Add(numbers, grouped);
        return numbers;
    }

    private static long? Append(long? number, int digit) =>
        number is not { } value ? digit : value < MaxNumber ? value * 10 + digit : value;

    private static void Add(HashSet<long> numbers, long? number)
    {
        if (number is { } value)
        {
            numbers.Add(value);
        }
    }

    /// <summary>
    /// A name a phone sent, as Granary shows it: cleaned as a discovered server's name is (no control, format or separator
    /// characters, compatibility forms folded), at most <see cref="MaxNameLength"/> characters.
    /// </summary>
    public static string CleanName(string? text) => LanAdverts.CleanLabel(text, MaxNameLength) ?? UnnamedPhone;
}
