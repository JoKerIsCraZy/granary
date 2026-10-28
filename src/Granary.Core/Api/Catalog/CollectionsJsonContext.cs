using System.Text.Json.Serialization;

namespace Granary.Core.Api.Catalog;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="CollectionsApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(PersonalCollectionList))]
[JsonSerializable(typeof(ServerCollectionList))]
[JsonSerializable(typeof(PersonalCollection))]
[JsonSerializable(typeof(PersonalCollectionCreate))]
[JsonSerializable(typeof(PersonalCollectionUpdate))]
[JsonSerializable(typeof(PersonalCollectionItemPage))]
[JsonSerializable(typeof(PersonalCollectionItemAdd))]
[JsonSerializable(typeof(PersonalCollectionOrder))]
[JsonSerializable(typeof(PersonalCollectionOrderChange))]
[JsonSerializable(typeof(CollectionCapabilities))]
[JsonSerializable(typeof(SmartQuery))]
[JsonSerializable(typeof(CollectionPreviewRequest))]
[JsonSerializable(typeof(CollectionPreview))]
[JsonSerializable(typeof(string))]
internal sealed partial class CollectionsJsonContext : JsonSerializerContext;
