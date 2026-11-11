using System.Text.Json.Serialization;

namespace Granary.Core.Api.Catalog;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="ThemesApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ThemeSongsCapability))]
[JsonSerializable(typeof(ThemePlaybackRequest))]
[JsonSerializable(typeof(ThemePlayback))]
internal sealed partial class ThemesJsonContext : JsonSerializerContext;
