using System.Text.Json.Serialization;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Discovery;
using Granary.Core.Api.Profiles;

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
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(Branding))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(TokenPair))]
[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(RefreshedTokens))]
[JsonSerializable(typeof(AuthProviderCollection))]
[JsonSerializable(typeof(DeviceLoginCapability))]
[JsonSerializable(typeof(StartDeviceLoginRequest))]
[JsonSerializable(typeof(DeviceLoginStart))]
[JsonSerializable(typeof(DeviceCodeRequest))]
[JsonSerializable(typeof(DeviceLoginPoll))]
[JsonSerializable(typeof(DeviceLoginCancel))]
[JsonSerializable(typeof(ProfileCollection))]
[JsonSerializable(typeof(ProfilePinCheck))]
[JsonSerializable(typeof(ProfileVerification))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(AccountPasswordCapability))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class SiloJsonContext : JsonSerializerContext;
