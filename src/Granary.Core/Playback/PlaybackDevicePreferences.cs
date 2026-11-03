using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>
/// This PC's own playback choices: they are about its hardware, so they stay with the PC, not with a server's profile.
/// </summary>
public interface IPlaybackDevicePreferences
{
    /// <summary>
    /// Direct play without full hardware decoding: HEVC, VP9, AV1 and HDR originals are played here, decoded on the CPU
    /// where the GPU cannot, instead of transcoded by the server (<see cref="Api.Playback.PlaybackReportBuilder.Build"/>).
    /// </summary>
    bool DecodesInSoftware { get; }

    /// <summary>The latest save: done once it is on disk, or failed and logged. The window waits for it briefly as it closes.</summary>
    Task LatestSave { get; }

    Task SetDecodesInSoftwareAsync(bool decodesInSoftware);
}

/// <summary>
/// <see cref="IPlaybackDevicePreferences"/> in <c>playback-device.json</c> in the data directory
/// (<c>{ "decodes_in_software": true }</c>). Without the file, or with one that cannot be read, direct play needs the GPU
/// as it does by default. One that cannot be written is logged, and the choice still holds for this run. Files are
/// written off the caller's thread, which is the UI's.
/// </summary>
public sealed class JsonPlaybackDevicePreferences : IPlaybackDevicePreferences
{
    private readonly string _filePath;
    private readonly ILogger<JsonPlaybackDevicePreferences> _logger;
    // Each save runs after the one before it on the thread pool, writing the choice as it is when its turn comes, so the
    // last one written is the latest and the latest save ends after every earlier one.
    private readonly Lock _saves = new();
    private volatile bool _decodesInSoftware;
    private Task _latestSave = Task.CompletedTask;

    public JsonPlaybackDevicePreferences(string filePath, ILogger<JsonPlaybackDevicePreferences> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        _decodesInSoftware = Load();
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "playback-device.json");

    public bool DecodesInSoftware => _decodesInSoftware;

    public Task LatestSave
    {
        get
        {
            lock (_saves)
            {
                return _latestSave;
            }
        }
    }

    public Task SetDecodesInSoftwareAsync(bool decodesInSoftware)
    {
        _decodesInSoftware = decodesInSoftware;
        return QueueSave();
    }

    // SaveAsync never throws, so the chain never breaks.
    private Task QueueSave()
    {
        lock (_saves)
        {
            return _latestSave = _latestSave.ContinueWith(_ => SaveAsync(), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }
    }

    private bool Load()
    {
        try
        {
            return File.Exists(_filePath)
                && JsonSerializer.Deserialize(File.ReadAllText(_filePath), PlaybackDeviceJsonContext.Default.PlaybackDeviceFile)?.DecodesInSoftware == true;
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts, and playback asks the GPU as by default.
            _logger.LogWarning(ex, "This PC's playback choices could not be read; direct play needs the GPU");
            return false;
        }
    }

    private async Task SaveAsync()
    {
        // Its own temporary file: a second Granary on the same data folder saving at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new PlaybackDeviceFile(_decodesInSoftware), PlaybackDeviceJsonContext.Default.PlaybackDeviceFile)
                    .ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the switch's to fail: the choice holds for this run.
            _logger.LogWarning(ex, "This PC's playback choices could not be saved");
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteFailure)
            {
                _logger.LogDebug(deleteFailure, "A half-written playback choices file stays behind");
            }
        }
    }
}

/// <summary><c>playback-device.json</c>.</summary>
internal sealed record PlaybackDeviceFile(bool? DecodesInSoftware);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(PlaybackDeviceFile))]
internal sealed partial class PlaybackDeviceJsonContext : JsonSerializerContext;
