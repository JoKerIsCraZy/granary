using System.Text.Json.Serialization;

namespace Granary.Core.Api.Accounts;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="DevicesApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(LoginSessionCollection))]
[JsonSerializable(typeof(LoginSessionCapabilities))]
[JsonSerializable(typeof(UserDeviceCollection))]
[JsonSerializable(typeof(UserDevice))]
[JsonSerializable(typeof(DeviceRename))]
internal sealed partial class DevicesJsonContext : JsonSerializerContext;
