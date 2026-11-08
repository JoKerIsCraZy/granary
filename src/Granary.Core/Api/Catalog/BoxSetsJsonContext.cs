using System.Text.Json.Serialization;

namespace Granary.Core.Api.Catalog;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="BoxSetsApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(CatalogItemBoxSet))]
[JsonSerializable(typeof(CatalogBoxSetDetail))]
internal sealed partial class BoxSetsJsonContext : JsonSerializerContext;
