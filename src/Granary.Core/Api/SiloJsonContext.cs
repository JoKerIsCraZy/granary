using System.Text.Json.Serialization;
using Granary.Core.Api.Discovery;

namespace Granary.Core.Api;

/// <summary>
/// Source-generated serializer settings for the Silo v2 wire format: snake_case members, optional
/// members omitted instead of sent as null, unknown members ignored on read.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(ServerIdentity))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class SiloJsonContext : JsonSerializerContext;
