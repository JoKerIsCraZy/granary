using System.Text.Json.Serialization;

namespace Granary.Core.Servers;

/// <summary>The on-disk shape of <c>servers.json</c>.</summary>
internal sealed record ServerRegistryFile(string? ActiveServerId, IReadOnlyList<ServerRegistryFileEntry>? Servers);

internal sealed record ServerRegistryFileEntry(
    string? Id,
    string? BaseUrl,
    string? Name,
    string? ServerId,
    DateTimeOffset AddedAt,
    DateTimeOffset LastUsedAt,
    string? LastProfileId);

/// <summary>
/// Serializer settings for the local server list. Separate from <c>SiloJsonContext</c> because the file
/// format is Granary's own, not the Silo wire format.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ServerRegistryFile))]
internal sealed partial class ServerRegistryJsonContext : JsonSerializerContext;
