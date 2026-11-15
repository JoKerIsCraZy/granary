using System.Text.Json.Serialization;

namespace Granary.Core.Remote;

/// <summary>
/// The phone remote's own JSON, apart from the server's snake_case <c>SiloJsonContext</c>: camelCase, members without a
/// value left out. The wire records read every member as nullable, because a member missing from the JSON sets an
/// <c>init</c> property to its default, not its initializer; <see cref="SiloCastMessages"/> checks the required ones by
/// hand. Text is written through a <c>Utf8JsonWriter</c> that escapes only what JSON must. Every wire record prints its
/// type's name only, as the public messages do: they carry names, addresses, ids and tokens.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SiloCastState))]
[JsonSerializable(typeof(WireHello))]
[JsonSerializable(typeof(WireControl))]
[JsonSerializable(typeof(WireLaunch))]
[JsonSerializable(typeof(WireOffer))]
[JsonSerializable(typeof(WireCancel))]
[JsonSerializable(typeof(WirePresence))]
[JsonSerializable(typeof(WireError))]
[JsonSerializable(typeof(WireReady))]
[JsonSerializable(typeof(WireClose))]
internal sealed partial class SiloCastJsonContext : JsonSerializerContext;

/// <summary><c>hello</c>, both ways: what a phone says and what Granary says.</summary>
internal sealed record WireHello
{
    public override string ToString() => nameof(WireHello);

    public string? Role { get; init; }

    public string? DeviceName { get; init; }

    public string? DeviceId { get; init; }

    public string? ServerId { get; init; }

    public string? ServerName { get; init; }

    public string? ServerIdentity { get; init; }

    public int[]? SupportedVersions { get; init; }

    public string?[]? Features { get; init; }

    public bool? Passive { get; init; }

    public string? ClientKind { get; init; }
}

/// <summary><c>control</c>: a command name and the one argument it takes.</summary>
internal sealed record WireControl
{
    public override string ToString() => nameof(WireControl);

    public string? Name { get; init; }

    public double? Seconds { get; init; }

    public long? TrackId { get; init; }

    public double? Speed { get; init; }

    public double? Volume { get; init; }

    public string? Value { get; init; }

    public bool? Enabled { get; init; }

    public int? Milliseconds { get; init; }
}

/// <summary><c>launch</c>.</summary>
internal sealed record WireLaunch
{
    public override string ToString() => nameof(WireLaunch);

    public string? ServerId { get; init; }

    public WirePlayback? Playback { get; init; }
}

/// <summary><c>launch.playback</c>. Track indexes are read wide, so one outside <c>int</c> is dropped, not malformed.</summary>
internal sealed record WirePlayback
{
    public override string ToString() => nameof(WirePlayback);

    public string? ContentId { get; init; }

    public long? FileId { get; init; }

    public long? AudioTrackIndex { get; init; }

    public long? SubtitleTrackIndex { get; init; }

    public bool? StartFromBeginning { get; init; }

    public double? ResumePosition { get; init; }

    public long? LibraryId { get; init; }

    public bool? PinsVersion { get; init; }
}

/// <summary>
/// <c>handoff_offer</c>, read only so far as to check it as Silo's Roku app does (<c>protocol.bs</c> <c>validOffer</c>).
/// The phone's addresses and tokens go no further than <see cref="SiloCastMessages.Decode"/>.
/// </summary>
internal sealed record WireOffer
{
    public override string ToString() => nameof(WireOffer);

    public string? RequestId { get; init; }

    public string? ServerId { get; init; }

    [JsonPropertyName("serverURL")]
    public string? ServerUrl { get; init; }

    public string? ServerName { get; init; }

    public string? ProfileId { get; init; }

    public string? ProfileName { get; init; }

    public string? Title { get; init; }

    public string? ServerIdentity { get; init; }

    public string? ResumeToken { get; init; }

    public bool? ReplaceConfirmed { get; init; }

    public string? ReplacingContentId { get; init; }
}

/// <summary><c>handoff_cancel</c>, both ways.</summary>
internal sealed record WireCancel
{
    public override string ToString() => nameof(WireCancel);

    public string? RequestId { get; init; }

    public string? Reason { get; init; }

    public string? Message { get; init; }
}

/// <summary><c>presence</c>.</summary>
internal sealed record WirePresence
{
    public override string ToString() => nameof(WirePresence);

    public bool? Passive { get; init; }
}

/// <summary><c>error</c>.</summary>
internal sealed record WireError(string Code, string Message)
{
    public override string ToString() => nameof(WireError);
}

/// <summary><c>handoff_ready</c>.</summary>
internal sealed record WireReady(string RequestId, string ServerId, string ProfileId, string SessionExpiresAt, bool Reused, string? ResumeToken)
{
    public override string ToString() => nameof(WireReady);
}

/// <summary><c>close</c>'s payload, written only for a reason.</summary>
internal sealed record WireClose(string Reason)
{
    public override string ToString() => nameof(WireClose);
}
