using System.Text.Json.Serialization;

namespace Granary.Core.Api.Catalog;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="LibraryPageApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(LibraryCollectionTab))]
[JsonSerializable(typeof(LibraryShuffleCapability))]
[JsonSerializable(typeof(LibraryShuffleRequest))]
[JsonSerializable(typeof(LibraryShuffle))]
internal sealed partial class LibraryPageJsonContext : JsonSerializerContext;
