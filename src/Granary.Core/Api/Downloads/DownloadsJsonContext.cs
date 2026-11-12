using System.Text.Json.Serialization;

namespace Granary.Core.Api.Downloads;

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the models of <see cref="DownloadsApi"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(DownloadCapability))]
[JsonSerializable(typeof(CreateDownloadBody))]
[JsonSerializable(typeof(DownloadCreated))]
[JsonSerializable(typeof(DownloadEntryPage))]
[JsonSerializable(typeof(DownloadEntry))]
[JsonSerializable(typeof(DownloadStatusEvent))]
[JsonSerializable(typeof(OfflineManifest))]
[JsonSerializable(typeof(ProgressSyncBody))]
[JsonSerializable(typeof(ProgressSyncResult))]
[JsonSerializable(typeof(ProgressListPage))]
internal sealed partial class DownloadsJsonContext : JsonSerializerContext;
