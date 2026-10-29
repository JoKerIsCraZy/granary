using System.Text.Json.Serialization;

namespace Granary.Core.Api.Subtitles;

/// <summary>The wire format of <see cref="SubtitleSyncApi"/> and the control lane's sync events: snake_case, unknown members ignored.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(SubtitleSyncStatus))]
[JsonSerializable(typeof(SubtitleSyncList))]
[JsonSerializable(typeof(SubtitleSyncEnvelope))]
[JsonSerializable(typeof(SubtitleTimingBody))]
[JsonSerializable(typeof(SubtitleSyncJob))]
[JsonSerializable(typeof(SubtitleTiming))]
internal sealed partial class SubtitleSyncJsonContext : JsonSerializerContext;
