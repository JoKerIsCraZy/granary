using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Granary.Core.Remote;

/// <summary>
/// A message a phone sends that a receiver handles, as <see cref="SiloCastMessages.Decode"/> reads it. Each keeps only
/// what Granary reads. <see cref="ToString"/> shows the kind and nothing it carries, so no name, id or address reaches a
/// log through it.
/// </summary>
public abstract record SiloCastMessage
{
    private protected SiloCastMessage()
    {
    }

    public sealed override string ToString() => GetType().Name;

    /// <summary>
    /// <c>hello</c>: who the peer is and which server it is signed in to. Read for any role; the receiver refuses all but a
    /// phone's.
    /// </summary>
    /// <param name="DeviceName">The name the phone gives itself, as it sent it.</param>
    /// <param name="DeviceId">The phone's own id: a newer connection with the same one replaces the older.</param>
    /// <param name="ServerId">The phone's server id, the server's address in base64url (<see cref="SiloCastServerId"/>).</param>
    /// <param name="ServerName">The phone's name for its server.</param>
    /// <param name="ServerIdentity">The deployment identity the phone verified, when it has one.</param>
    /// <param name="SupportedVersions">The protocol versions the peer speaks.</param>
    /// <param name="Features">Optional behaviours the peer lists (<see cref="SiloCastFeatures"/>).</param>
    /// <param name="Passive">A controller that only watches (a silent reattach) and is not actively controlling.</param>
    /// <param name="Role"><c>phone</c> or <c>tv</c>.</param>
    public sealed record PhoneHello(
        string DeviceName,
        string DeviceId,
        string? ServerId,
        string? ServerName,
        string? ServerIdentity,
        IReadOnlyList<int> SupportedVersions,
        IReadOnlySet<string> Features,
        bool Passive,
        string Role) : SiloCastMessage;

    /// <summary><c>control</c>: a command of <see cref="SiloCastControls"/> and the one argument it takes, if any.</summary>
    public sealed record Control(
        string Name,
        double? Seconds,
        long? TrackId,
        double? Speed,
        double? Volume,
        string? Value,
        bool? Enabled,
        int? Milliseconds) : SiloCastMessage;

    /// <summary><c>launch</c>: start a title, after a ready offer on the same connection.</summary>
    public sealed record Launch(string ServerId, LaunchPlayback Playback) : SiloCastMessage;

    /// <summary>
    /// <c>handoff_offer</c>, without the phone's server address, other addresses, profile name, title or resume token:
    /// nothing a phone names is ever contacted, and a token sent back never stands in for the profile check.
    /// </summary>
    public sealed record HandoffOffer(
        string RequestId,
        string ServerId,
        string? ServerIdentity,
        string ProfileId,
        bool ReplaceConfirmed,
        string? ReplacingContentId) : SiloCastMessage;

    /// <summary><c>handoff_cancel</c> from the phone: it drops the offer it made.</summary>
    public sealed record HandoffCancelled(string RequestId) : SiloCastMessage;

    /// <summary><c>presence</c>: the phone's controller became passive or active.</summary>
    public sealed record Presence(bool Passive) : SiloCastMessage;

    /// <summary><c>ping</c>, answered with <c>pong</c>.</summary>
    public sealed record Ping() : SiloCastMessage;

    /// <summary><c>pong</c>, the answer to the receiver's ping.</summary>
    public sealed record Pong() : SiloCastMessage;

    /// <summary><c>close</c>, with its reason when the phone gave one.</summary>
    public sealed record Close(string? Reason) : SiloCastMessage;

    /// <summary>
    /// A type the receiver does not handle (a newer phone's, or one only a receiver sends), or a <c>control</c> with a
    /// name it does not know: skipped, and the connection stays.
    /// </summary>
    public sealed record Unknown(string Type) : SiloCastMessage;
}

/// <summary>
/// The title a <c>launch</c> names. <paramref name="FileId"/> is the version, as text; track indexes are those the phone
/// sends a receiver of the Roku type (the combined ordinal, negative for subtitles off).
/// </summary>
public sealed record LaunchPlayback(
    string ContentId,
    string? FileId,
    int? AudioTrackIndex,
    int? SubtitleTrackIndex,
    bool StartFromBeginning,
    double? ResumePosition,
    bool PinsVersion)
{
    public override string ToString() => nameof(LaunchPlayback);
}

/// <summary>
/// Reading and writing the messages of Silo's phone remote: UTF-8 JSON in the envelope
/// <c>{"type": "&lt;kind&gt;", "v": 2, "&lt;kindKey&gt;": {…}}</c>, the payload under the kind in camelCase
/// (<c>handoffOffer</c> for <c>handoff_offer</c>), as silo-roku's <c>protocol.bs</c>, silo-android's
/// <c>SiloCastMessage.kt</c> and silo-apple's <c>SiloControlProtocol.swift</c> have it. Members without a value are
/// left out; unknown members are ignored.
/// </summary>
public static class SiloCastMessages
{
    /// <summary>The protocol version: the only one a receiver of the Roku type speaks.</summary>
    public const int Version = 2;

    // Deep enough for any message a phone sends; a member twice is not one message.
    private static readonly JsonDocumentOptions Reading = new() { MaxDepth = 16, AllowDuplicateProperties = false };

    // Only what JSON must escape: names such as "Elena's PC" go out as they are.
    private static readonly JsonWriterOptions Writing = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly int[] SupportedVersions = [Version];

    /// <summary>
    /// The message in a frame, <see cref="SiloCastMessage.Unknown"/> for a type the receiver does not handle or a control
    /// it does not know, and null for a malformed frame: not a JSON object with a text <c>type</c>, too deep, a member
    /// twice, or a known type whose payload lacks a required member or has one of the wrong kind. A malformed frame ends
    /// the connection; an unknown one is skipped (silo-roku's <c>protocol.bs</c> <c>decode</c>).
    /// </summary>
    public static SiloCastMessage? Decode(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), Reading);
            var frame = document.RootElement;
            if (frame.ValueKind != JsonValueKind.Object
                || !frame.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return type.GetString() switch
            {
                "ping" => new SiloCastMessage.Ping(),
                "pong" => new SiloCastMessage.Pong(),
                "close" => ReadClose(frame),
                "hello" => Payload(frame, "hello", SiloCastJsonContext.Default.WireHello) is { } hello ? ReadHello(hello) : null,
                "control" => ReadControl(frame),
                "launch" => Payload(frame, "launch", SiloCastJsonContext.Default.WireLaunch) is { } launch ? ReadLaunch(launch) : null,
                "handoff_offer" => Payload(frame, "handoffOffer", SiloCastJsonContext.Default.WireOffer) is { } offer ? ReadOffer(offer) : null,
                "handoff_cancel" => Payload(frame, "handoffCancel", SiloCastJsonContext.Default.WireCancel) is { RequestId: { } id, Reason: not null }
                    ? new SiloCastMessage.HandoffCancelled(id)
                    : null,
                "presence" => Payload(frame, "presence", SiloCastJsonContext.Default.WirePresence) is { Passive: { } passive }
                    ? new SiloCastMessage.Presence(passive)
                    : null,
                // state, error, handoff_ready and handoff_challenge come only from a receiver; anything else is a newer peer's.
                { } other => new SiloCastMessage.Unknown(other),
                null => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Granary's <c>hello</c>: role <c>tv</c>, as every Silo receiver says, version 2 and the five receiver features.</summary>
    public static byte[] Hello(ReceiverIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var hello = new WireHello
        {
            Role = SiloCastRoles.Tv,
            DeviceName = identity.Name,
            DeviceId = identity.ReceiverId,
            ServerId = identity.ServerId,
            ServerName = identity.ServerName,
            ServerIdentity = string.IsNullOrEmpty(identity.ServerIdentity) ? null : identity.ServerIdentity,
            SupportedVersions = SupportedVersions,
            Features = [.. SiloCastFeatures.Receiver],
        };
        return Envelope("hello", "hello", hello, SiloCastJsonContext.Default.WireHello);
    }

    /// <summary><c>state</c>.</summary>
    public static byte[] State(SiloCastState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Envelope("state", "state", state, SiloCastJsonContext.Default.SiloCastState);
    }

    /// <summary><c>error</c> with a code of <see cref="SiloCastErrors"/> and the message the phone shows as it is.</summary>
    public static byte[] Error(string code, string message) =>
        Envelope("error", "error", new WireError(code, message), SiloCastJsonContext.Default.WireError);

    /// <summary>
    /// <c>handoff_ready</c> for a profile Granary is already signed in as: <c>reused</c>, with the session's end in UTC to
    /// the second (required, though nothing reads it) and a resume token.
    /// </summary>
    public static byte[] HandoffReady(string requestId, string serverId, string profileId, DateTimeOffset sessionExpiresAt, string resumeToken)
    {
        string expires = sessionExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var ready = new WireReady(requestId, serverId, profileId, expires, Reused: true, resumeToken);
        return Envelope("handoff_ready", "handoffReady", ready, SiloCastJsonContext.Default.WireReady);
    }

    /// <summary><c>handoff_cancel</c> with a reason of <see cref="SiloCastCancelReasons"/> and the message the phone shows.</summary>
    public static byte[] HandoffCancel(string requestId, string reason, string message) =>
        Envelope("handoff_cancel", "handoffCancel", new WireCancel { RequestId = requestId, Reason = reason, Message = message },
            SiloCastJsonContext.Default.WireCancel);

    public static byte[] Ping() => Envelope<WireClose>("ping", null, null, null);

    public static byte[] Pong() => Envelope<WireClose>("pong", null, null, null);

    /// <summary>
    /// <c>close</c>, with a reason of <see cref="SiloCastCloseReasons"/> or bare: the reason goes only to a peer that lists
    /// <c>close_reason</c>.
    /// </summary>
    public static byte[] Close(string? reason) =>
        reason is null
            ? Envelope<WireClose>("close", null, null, null)
            : Envelope("close", "close", new WireClose(reason), SiloCastJsonContext.Default.WireClose);

    private static byte[] Envelope<T>(string type, string? payloadKey, T? payload, JsonTypeInfo<T>? payloadType)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, Writing))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteNumber("v", Version);
            if (payloadKey is not null && payload is not null && payloadType is not null)
            {
                writer.WritePropertyName(payloadKey);
                JsonSerializer.Serialize(writer, payload, payloadType);
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // The payload under its key, or null when it is missing or not an object; a member of the wrong kind throws.
    private static T? Payload<T>(JsonElement frame, string key, JsonTypeInfo<T> type) where T : class =>
        frame.TryGetProperty(key, out var payload) && payload.ValueKind == JsonValueKind.Object ? payload.Deserialize(type) : null;

    // Read leniently, as the phones read it: a payload that is not { "reason": "<text>" } is no reason.
    private static SiloCastMessage.Close ReadClose(JsonElement frame) =>
        new(frame.TryGetProperty("close", out var close) && close.ValueKind == JsonValueKind.Object
            && close.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null);

    // As silo-roku's validHello: role phone or tv, a name and an id, a list of versions, features a list of text.
    private static SiloCastMessage.PhoneHello? ReadHello(WireHello hello)
    {
        if (hello is not { Role: SiloCastRoles.Phone or SiloCastRoles.Tv, DeviceName: { } name, DeviceId: { } id, SupportedVersions: { } versions }
            || hello.Features?.Any(feature => feature is null) == true)
        {
            return null;
        }
        var features = new HashSet<string>(hello.Features?.OfType<string>() ?? [], StringComparer.Ordinal);
        return new SiloCastMessage.PhoneHello(
            name, id, hello.ServerId, hello.ServerName, hello.ServerIdentity, versions, features, hello.Passive == true, hello.Role);
    }

    private static SiloCastMessage? ReadControl(JsonElement frame)
    {
        if (!frame.TryGetProperty("control", out var command) || command.ValueKind != JsonValueKind.Object
            || !command.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        // A name this build does not know is skipped before its arguments are looked at (a retired set_hdr_enabled included).
        if (!SiloCastControls.IsKnown(name.GetString()))
        {
            return new SiloCastMessage.Unknown("control");
        }
        var control = command.Deserialize(SiloCastJsonContext.Default.WireControl);
        return control is { Name: { } known }
            ? new SiloCastMessage.Control(known, control.Seconds, control.TrackId, control.Speed, control.Volume, control.Value,
                control.Enabled, control.Milliseconds)
            : null;
    }

    // As silo-roku's validLaunch: a server, a title and whether from the beginning; integers where integers belong.
    private static SiloCastMessage.Launch? ReadLaunch(WireLaunch launch)
    {
        if (launch is not { ServerId: { } serverId, Playback: { ContentId: { } contentId, StartFromBeginning: { } fromStart } playback })
        {
            return null;
        }
        return new SiloCastMessage.Launch(serverId, new LaunchPlayback(
            contentId,
            playback.FileId?.ToString(CultureInfo.InvariantCulture),
            AsIndex(playback.AudioTrackIndex),
            AsIndex(playback.SubtitleTrackIndex),
            fromStart,
            playback.ResumePosition,
            playback.PinsVersion == true));
    }

    // As silo-roku's validOffer: request, server, the server's address and profile as text. The address is checked, not kept.
    private static SiloCastMessage.HandoffOffer? ReadOffer(WireOffer offer) =>
        offer is { RequestId: { } requestId, ServerId: { } serverId, ServerUrl: not null, ProfileId: { } profileId }
            ? new SiloCastMessage.HandoffOffer(requestId, serverId, offer.ServerIdentity, profileId, offer.ReplaceConfirmed == true,
                offer.ReplacingContentId)
            : null;

    // An index outside int is dropped, as one Granary cannot pass on.
    private static int? AsIndex(long? index) => index is >= int.MinValue and <= int.MaxValue ? (int)index.Value : null;
}

/// <summary>The <c>hello</c> roles.</summary>
public static class SiloCastRoles
{
    public const string Phone = "phone";

    /// <summary>What every Silo receiver says, the Roku type included: there is no role of its own.</summary>
    public const string Tv = "tv";
}

/// <summary>The command names of <c>control</c>, closed as silo-apple's <c>SiloControlCommand.Name</c> and silo-roku's <c>commandNames</c>.</summary>
public static class SiloCastControls
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string PlayPause = "play_pause";

    /// <summary><c>seconds</c>, absolute, in the title's own time.</summary>
    public const string Seek = "seek";

    public const string Stop = "stop";

    /// <summary><c>trackId</c>.</summary>
    public const string SelectAudioTrack = "select_audio_track";

    /// <summary><c>trackId</c>, absent for off.</summary>
    public const string SelectSubtitleTrack = "select_subtitle_track";

    /// <summary><c>speed</c>.</summary>
    public const string SetPlaybackSpeed = "set_playback_speed";

    /// <summary><c>value</c>, a quality option's id.</summary>
    public const string SetQuality = "set_quality";

    /// <summary><c>value</c>: <c>fit</c> or <c>fill</c>.</summary>
    public const string SetVideoGravity = "set_video_gravity";

    /// <summary><c>milliseconds</c>.</summary>
    public const string SetSubtitleSyncMs = "set_subtitle_sync_ms";

    /// <summary><c>value</c>: <c>bottom</c>, <c>lower-third</c> or <c>top</c>.</summary>
    public const string SetSubtitlePosition = "set_subtitle_position";

    /// <summary><c>volume</c>, 0 to 1.</summary>
    public const string SetVolume = "set_volume";

    /// <summary><c>enabled</c>.</summary>
    public const string SetMuted = "set_muted";

    public const string PlayNext = "play_next";

    /// <summary>Every name a phone sends.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Play, Pause, PlayPause, Seek, Stop, SelectAudioTrack, SelectSubtitleTrack, SetPlaybackSpeed, SetQuality, SetVideoGravity,
        SetSubtitleSyncMs, SetSubtitlePosition, SetVolume, SetMuted, PlayNext,
    };

    public static bool IsKnown(string? name) => name is not null && All.Contains(name);
}

/// <summary>The codes of <c>error</c> that a receiver sends.</summary>
public static class SiloCastErrors
{
    public const string Unauthorized = "unauthorized";
    public const string VersionUnsupported = "version_unsupported";
    public const string ServerMismatch = "server_mismatch";
    public const string PlayerNotReady = "player_not_ready";
    public const string CommandFailed = "command_failed";
    public const string RateLimited = "rate_limited";
    public const string TooManyControllers = "too_many_controllers";
    public const string HandoffRequired = "handoff_required";
    public const string LaunchPending = "launch_pending";
    public const string LaunchFailed = "launch_failed";
    public const string WatchPartyActive = "watch_party_active";
}

/// <summary>The reasons of <c>close</c>, sent only to a peer that lists <c>close_reason</c>.</summary>
public static class SiloCastCloseReasons
{
    /// <summary>A newer connection of the same phone took this one's place.</summary>
    public const string Replaced = "replaced";

    /// <summary>The PC let the phone go: Disconnect Remote, the switch, a stop.</summary>
    public const string TvDisconnected = "tv_disconnected";

    public const string TooManyControllers = "too_many_controllers";

    /// <summary>No hello in time, or no answer to the pings.</summary>
    public const string Timeout = "timeout";

    /// <summary>No key, or a hello the receiver does not take.</summary>
    public const string Unauthorized = "unauthorized";
}

/// <summary>The reasons of <c>handoff_cancel</c>.</summary>
public static class SiloCastCancelReasons
{
    public const string Superseded = "superseded";
    public const string ReplaceConfirmationRequired = "replace_confirmation_required";
    public const string WatchPartyActive = "watch_party_active";
    public const string VersionUnsupported = "version_unsupported";
    public const string LaunchPending = "launch_pending";
    public const string HandoffFailed = "handoff_failed";
    public const string ControllerCancelled = "controller_cancelled";
}

/// <summary>The names of <c>hello.features</c>.</summary>
public static class SiloCastFeatures
{
    /// <summary>The receiver keeps other controllers connected: a close concerns this phone alone.</summary>
    public const string MultiController = "multi_controller";

    /// <summary>A close may carry its reason.</summary>
    public const string CloseReason = "close_reason";

    /// <summary>The receiver issues a resume token with <c>handoff_ready</c>.</summary>
    public const string ResumeToken = "resume_token";

    /// <summary>The receiver may ask before replacing another profile's title.</summary>
    public const string ReplaceConfirmation = "replace_confirmation";

    /// <summary>The receiver takes <c>presence</c>.</summary>
    public const string Presence = "presence";

    /// <summary>
    /// What Granary lists, as every Silo receiver does, without the Roku app's own <c>volume_mute_only</c> and
    /// <c>no_playback_speed</c>: Granary's player sets a level and a speed.
    /// </summary>
    public static IReadOnlyList<string> Receiver { get; } = [MultiController, CloseReason, ResumeToken, ReplaceConfirmation, Presence];
}
