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

    /// <summary>What Up and Down do in the player while focus is on the picture.</summary>
    PlayerUpDown UpDown { get; }

    Task SetDecodesInSoftwareAsync(bool decodesInSoftware);

    Task SetUpDownAsync(PlayerUpDown upDown);
}

/// <summary>Up and Down in the player, with focus on the picture.</summary>
public enum PlayerUpDown
{
    /// <summary>
    /// As on Silo's TV app: Up shows the controls with focus on Play/Pause, Down opens audio, subtitles or the settings.
    /// A remote has its own volume buttons.
    /// </summary>
    ControlsAndMenus,

    /// <summary>As the keyboard shortcuts of Silo's web player: the volume, 5 % a press.</summary>
    Volume,
}

/// <summary>
/// <see cref="IPlaybackDevicePreferences"/> in <c>playback-device.json</c> in the data directory
/// (<c>{ "decodes_in_software": true, "up_down": "volume" }</c>). Without the file, or with one that cannot be read, direct
/// play needs the GPU and Up and Down show the controls and menus, as by default; a value it does not know is the default. One that cannot be written is logged, and the choice still holds for this run. Files are
/// written off the caller's thread, which is the UI's.
/// </summary>
public sealed class JsonPlaybackDevicePreferences : IPlaybackDevicePreferences
{
    private readonly string _filePath;
    private readonly ILogger<JsonPlaybackDevicePreferences> _logger;
    // Saves one at a time, each the choice as it is when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const string VolumeValue = "volume";
    private const string ControlsValue = "controls";

    private volatile bool _decodesInSoftware;
    private volatile PlayerUpDown _upDown;

    public JsonPlaybackDevicePreferences(string filePath, ILogger<JsonPlaybackDevicePreferences> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        var file = Load();
        _decodesInSoftware = file?.DecodesInSoftware == true;
        _upDown = file?.UpDown == VolumeValue ? PlayerUpDown.Volume : PlayerUpDown.ControlsAndMenus;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "playback-device.json");

    public bool DecodesInSoftware => _decodesInSoftware;

    public PlayerUpDown UpDown => _upDown;

    public Task SetDecodesInSoftwareAsync(bool decodesInSoftware)
    {
        _decodesInSoftware = decodesInSoftware;
        return Task.Run(SaveAsync);
    }

    public Task SetUpDownAsync(PlayerUpDown upDown)
    {
        _upDown = upDown;
        return Task.Run(SaveAsync);
    }

    private PlaybackDeviceFile? Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_filePath), PlaybackDeviceJsonContext.Default.PlaybackDeviceFile)
                : null;
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts with the defaults.
            _logger.LogWarning(ex, "This PC's playback choices could not be read; the defaults apply");
            return null;
        }
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        string temp = _filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                var file = new PlaybackDeviceFile(_decodesInSoftware, _upDown == PlayerUpDown.Volume ? VolumeValue : ControlsValue);
                await JsonSerializer.SerializeAsync(stream, file, PlaybackDeviceJsonContext.Default.PlaybackDeviceFile).ConfigureAwait(false);
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
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary><c>playback-device.json</c>.</summary>
internal sealed record PlaybackDeviceFile(bool? DecodesInSoftware, string? UpDown);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(PlaybackDeviceFile))]
internal sealed partial class PlaybackDeviceJsonContext : JsonSerializerContext;
