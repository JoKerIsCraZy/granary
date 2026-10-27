using System.Text.Json.Serialization;

namespace Granary.Core.Api.Admin;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="AdminApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(AdminDashboardStats))]
[JsonSerializable(typeof(AdminServerStatus))]
[JsonSerializable(typeof(AdminBuildInfo))]
[JsonSerializable(typeof(AdminNodeCollection))]
[JsonSerializable(typeof(AdminPlaybackSessionCollection))]
[JsonSerializable(typeof(AdminDashboardTimeseries))]
[JsonSerializable(typeof(AdminDashboardPlaybackActivity))]
[JsonSerializable(typeof(AdminDashboardTopActivity))]
[JsonSerializable(typeof(AdminUserCollection))]
[JsonSerializable(typeof(AdminLibraryCollection))]
[JsonSerializable(typeof(AdminPlaybackCommand))]
[JsonSerializable(typeof(AdminPlaybackCommandReceipt))]
[JsonSerializable(typeof(AdminPlaybackTerminate))]
[JsonSerializable(typeof(AdminPlaybackTerminateReceipt))]
[JsonSerializable(typeof(AdminIpUserCollection))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;
