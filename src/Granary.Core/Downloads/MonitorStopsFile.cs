using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>A monitor's stop that has not reached the server yet: its profile, its id and the validator it goes under.</summary>
public sealed record PendingStop(DownloadScope Scope, string SubscriptionId, string Etag);

/// <summary>
/// <c>monitor-stops.json</c> in the data folder: the monitor stops that have not reached the server yet, so one made while the
/// server could not be reached is still sent after Granary starts again, as Silo's Apple app keeps them
/// (<c>pendingSubscriptionDeletes</c>), and the list does not bring the monitor back meanwhile. It holds ids and validators,
/// nothing secret. A file that cannot be read is no stops, and logged; one that cannot be written is logged, and the stops hold
/// for the run.
/// </summary>
public sealed class MonitorStopsFile(string path, ILogger<MonitorStopsFile> logger)
{
    private readonly string _path = Path.GetFullPath(path);

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "monitor-stops.json");

    /// <summary>The stops the file keeps; none without it. Never throws.</summary>
    public IReadOnlyList<PendingStop> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_path), MonitorStopsJsonContext.Default.MonitorStopsData);
            // A member a hand or a damaged file left out makes that stop no stop.
            return [.. (file?.Stops ?? []).Where(s => s is { Server.Length: > 0, Account.Length: > 0, Profile.Length: > 0, Id.Length: > 0, Etag.Length: > 0 })
                .Select(s => new PendingStop(new DownloadScope(s.Server!, s.Account!, s.Profile!), s.Id!, s.Etag!))];
        }
        catch (Exception ex)
        {
            logger.LogWarning("The monitor stops still to send could not be read ({Failure}); none are sent", DownloadLog.Failure(ex));
            return [];
        }
    }

    /// <summary>Keeps <paramref name="stops"/> as the file's whole content; none deletes the file. Never throws.</summary>
    public void Save(IReadOnlyList<PendingStop> stops)
    {
        try
        {
            if (stops.Count == 0)
            {
                File.Delete(_path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var data = new MonitorStopsData([.. stops.Select(s => new MonitorStopData(s.Scope.ServerEntryId, s.Scope.AccountId, s.Scope.ProfileId,
                s.SubscriptionId, s.Etag))]);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(data, MonitorStopsJsonContext.Default.MonitorStopsData);
            SafeFile.WriteWhole(_path, temp => File.WriteAllBytes(temp, json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The monitor stops still to send could not be saved ({Failure}); they hold for this run", DownloadLog.Failure(ex));
        }
    }
}

internal sealed record MonitorStopsData(IReadOnlyList<MonitorStopData>? Stops);

internal sealed record MonitorStopData(string? Server, string? Account, string? Profile, string? Id, string? Etag);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(MonitorStopsData))]
internal sealed partial class MonitorStopsJsonContext : JsonSerializerContext;
