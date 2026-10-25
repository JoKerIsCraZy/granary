using System.Text.Json.Serialization;

namespace Granary.Core.Api.Catalog;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="CollectionsApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(PersonalCollectionList))]
[JsonSerializable(typeof(ServerCollectionList))]
internal sealed partial class CollectionsJsonContext : JsonSerializerContext;
