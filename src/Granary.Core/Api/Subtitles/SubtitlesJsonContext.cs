using System.Text.Json.Serialization;

namespace Granary.Core.Api.Subtitles;

/// <summary>The wire format of <see cref="SubtitlesApi"/>: snake_case members, nulls left out on write, unknown members ignored on read.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(SubtitleProviderStatus))]
[JsonSerializable(typeof(SubtitleSearchBody))]
[JsonSerializable(typeof(SubtitleSearchResults))]
[JsonSerializable(typeof(SubtitleDownloadBody))]
[JsonSerializable(typeof(SubtitleDownloadResult))]
[JsonSerializable(typeof(SubtitleDetection))]
internal sealed partial class SubtitlesJsonContext : JsonSerializerContext;
