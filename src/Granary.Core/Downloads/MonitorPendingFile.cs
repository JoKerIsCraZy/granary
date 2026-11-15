using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>A monitor's stop that has not reached the server yet: its profile, its id and the validator it goes under.</summary>
public sealed record PendingStop(DownloadScope Scope, string SubscriptionId, string Etag);

/// <summary>A create's choice the server's monitor does not show yet (<see cref="UnconfirmedChoice"/>), with its profile and monitor.</summary>
public sealed record PendingChoice(DownloadScope Scope, string SubscriptionId, UnconfirmedChoice Choice);

/// <summary>What <see cref="MonitorPendingFile"/> keeps: the stops and the choices still to go.</summary>
public sealed record MonitorPending(IReadOnlyList<PendingStop> Stops, IReadOnlyList<PendingChoice> Choices)
{
    public static MonitorPending None { get; } = new([], []);
}

/// <summary>
/// <c>monitor-pending.json</c> in the data folder: the monitor writes the server has not taken yet, kept across starts. The
/// stops (as Silo's Apple app keeps them, <c>pendingSubscriptionDeletes</c>): one made while the server could not be reached is
/// still sent after Granary starts again, and the list does not bring its monitor back meanwhile. And the choices a create made
/// that the server's monitor does not show yet, so a restart does not let the monitor's old Delete after watching delete what
/// the viewer chose to keep. It holds ids, validators and those choices, nothing secret.
/// </summary>
public sealed class MonitorPendingFile(string path, ILogger<MonitorPendingFile> logger)
{
    private readonly string _path = Path.GetFullPath(path);

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "monitor-pending.json");

    /// <summary>
    /// What the file keeps; nothing without it, or for a file that is not one (damaged, logged). Null when it is there but cannot
    /// be read now (another program holds it, or this user may not look at it): it is read again later, and never written over
    /// meanwhile. Never throws.
    /// </summary>
    public MonitorPending? Load()
    {
        string json;
        try
        {
            // Read straight away: File.Exists says false also for a file this user may not look at, which is not "none".
            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return MonitorPending.None;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            logger.LogWarning("The monitor writes still to send could not be read now ({Failure}); they are read again later", DownloadLog.Failure(ex));
            return null;
        }
        try
        {
            var file = JsonSerializer.Deserialize(json, MonitorPendingJsonContext.Default.MonitorPendingData);
            // A member a hand or a damaged file left out makes that entry no entry.
            var stops = (file?.Stops ?? []).Where(s => s is { Server.Length: > 0, Account.Length: > 0, Profile.Length: > 0, Id.Length: > 0, Etag.Length: > 0 })
                .Select(s => new PendingStop(new DownloadScope(s.Server!, s.Account!, s.Profile!), s.Id!, s.Etag!));
            var choices = (file?.Choices ?? []).Where(c => c is { Server.Length: > 0, Account.Length: > 0, Profile.Length: > 0, Id.Length: > 0, Mode.Length: > 0 })
                .Select(c => new PendingChoice(new DownloadScope(c.Server!, c.Account!, c.Profile!), c.Id!,
                    new UnconfirmedChoice(new MonitorChoice(c.Mode!, c.DeleteWatched, Math.Max(0, c.MaxStorageBytes), c.Quality), c.ServerDeleteWatched)));
            return new MonitorPending([.. stops], [.. choices]);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("The monitor writes still to send were not readable ({Failure}); none are sent", DownloadLog.Failure(ex));
            return MonitorPending.None;
        }
    }

    /// <summary>Keeps <paramref name="pending"/> as the file's whole content; with nothing left, the file goes. Never throws.</summary>
    public void Save(MonitorPending pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        try
        {
            if (pending.Stops.Count == 0 && pending.Choices.Count == 0)
            {
                File.Delete(_path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var data = new MonitorPendingData(
                [.. pending.Stops.Select(s => new MonitorStopData(s.Scope.ServerEntryId, s.Scope.AccountId, s.Scope.ProfileId, s.SubscriptionId, s.Etag))],
                [.. pending.Choices.Select(c => new MonitorChoiceData(c.Scope.ServerEntryId, c.Scope.AccountId, c.Scope.ProfileId, c.SubscriptionId,
                    c.Choice.ServerDeleteWatched, c.Choice.Choice.Mode, c.Choice.Choice.DeleteWatched, c.Choice.Choice.MaxStorageBytes, c.Choice.Choice.Quality))]);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(data, MonitorPendingJsonContext.Default.MonitorPendingData);
            SafeFile.WriteWhole(_path, temp => File.WriteAllBytes(temp, json));
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            logger.LogWarning("The monitor writes still to send could not be saved ({Failure}); they hold for this run", DownloadLog.Failure(ex));
        }
    }

    // What reading or writing the file can meet on a path that is valid, beside I/O and access errors: a path form or a policy
    // the system does not allow. Each is a file that cannot be used now, never a bug of the caller's.
    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;
}

internal sealed record MonitorPendingData(IReadOnlyList<MonitorStopData>? Stops, IReadOnlyList<MonitorChoiceData>? Choices);

internal sealed record MonitorStopData(string? Server, string? Account, string? Profile, string? Id, string? Etag);

internal sealed record MonitorChoiceData(string? Server, string? Account, string? Profile, string? Id, bool ServerDeleteWatched, string? Mode, bool DeleteWatched,
    long MaxStorageBytes, string? Quality);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(MonitorPendingData))]
internal sealed partial class MonitorPendingJsonContext : JsonSerializerContext;
