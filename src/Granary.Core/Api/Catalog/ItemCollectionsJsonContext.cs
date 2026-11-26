using System.Text.Json.Serialization;

namespace Granary.Core.Api.Catalog;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="ItemCollectionsApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(CatalogItemCollections))]
internal sealed partial class ItemCollectionsJsonContext : JsonSerializerContext;
