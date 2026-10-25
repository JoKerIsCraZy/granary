using System.Text.Json.Serialization;

namespace Granary.Core.Api.Playback;

/// <summary>
/// The wire format of <see cref="SiloJsonContext"/> for <see cref="WatchApi"/> and <see cref="PlaybackApi"/>: snake_case
/// members, nulls left out on write (the strict playback bodies reject an explicit null), unknown members ignored on read.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(WatchDetail))]
[JsonSerializable(typeof(PlaybackCapabilities))]
[JsonSerializable(typeof(PlaybackStartRequest))]
[JsonSerializable(typeof(PlaybackReplanRequest))]
[JsonSerializable(typeof(PlaybackDecision))]
[JsonSerializable(typeof(PlaybackProgressRequest))]
[JsonSerializable(typeof(PlaybackStopRequest))]
[JsonSerializable(typeof(PlaybackMutation))]
[JsonSerializable(typeof(PlaybackRouteEvent))]
[JsonSerializable(typeof(PlaybackRouteEventReceipt))]
internal sealed partial class PlaybackJsonContext : JsonSerializerContext;
