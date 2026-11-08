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

    /// <summary>The player's time ends with the time left, not the duration: the viewer's last click on it; off by default.</summary>
    bool TimeLeft { get; }

    /// <summary>
    /// How many episodes in a row start by themselves before the player waits for the viewer, 0 for never
    /// (<c>player.passout_threshold</c>, client-local in the contract).
    /// </summary>
    int PassoutThreshold { get; }

    /// <summary>
    /// How many seconds before the saved point a resume starts, 0 to 30 (<c>player.resume_rewind_seconds</c>, client-local
    /// in the contract); 0, the exact point, by default (<see cref="ResumeRewind.DefaultSeconds"/>).
    /// </summary>
    int ResumeRewindSeconds { get; }

    /// <summary>Discord's status shows what plays (<see cref="ViewModels.Player.PlayerRichPresence"/>); off by default.</summary>
    bool DiscordStatus { get; }

    /// <summary>Raised on the caller's thread when <see cref="DiscordStatus"/> was turned on or off, so a title that plays follows.</summary>
    event EventHandler? DiscordStatusChanged;

    /// <summary>The player has its Sleep button (<see cref="ViewModels.PlayerViewModel.ShowsSleepTimer"/>); on by default.</summary>
    bool SleepTimerButton { get; }

    /// <summary>
    /// Raised on the caller's thread with the new choice when <see cref="SleepTimerButton"/> was turned on or off, so an
    /// open player follows each change in order, even after the next one.
    /// </summary>
    event EventHandler<bool>? SleepTimerButtonChanged;

    /// <summary>The latest save: done once it is on disk, or failed and logged. The window waits for it briefly as it closes.</summary>
    Task LatestSave { get; }

    Task SetDecodesInSoftwareAsync(bool decodesInSoftware);

    Task SetUpDownAsync(PlayerUpDown upDown);

    Task SetTimeLeftAsync(bool shown);

    Task SetPassoutThresholdAsync(int episodes);

    /// <summary>Saves <see cref="ResumeRewindSeconds"/>, brought into 0 to 30 first.</summary>
    Task SetResumeRewindSecondsAsync(int seconds);

    Task SetDiscordStatusAsync(bool enabled);

    Task SetSleepTimerButtonAsync(bool shown);
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
/// (<c>{ "decodes_in_software": true, "up_down": "volume", "time_left": true, "passout_threshold": 5,
/// "resume_rewind_seconds": 7, "discord_status": true, "sleep_timer_button": false }</c>).
/// Without the file, or with one that cannot be read, direct play needs the GPU, Up and Down show the controls and menus,
/// the player's time shows the duration, the player waits after three episodes, a resume lands on the saved point,
/// Discord's status stays as it is and the player has its Sleep button, as by default; a value it does not know, or one an
/// older file lacks, is the default. One that cannot be written is logged, and the choice still holds for this run. Files
/// are written off the caller's thread, which is the UI's.
/// </summary>
public sealed class JsonPlaybackDevicePreferences : IPlaybackDevicePreferences
{
    private readonly string _filePath;
    private readonly ILogger<JsonPlaybackDevicePreferences> _logger;
    private const string VolumeValue = "volume";
    private const string ControlsValue = "controls";

    // Each save runs after the one before it on the thread pool, writing the choices as they are when its turn comes, so
    // the last one written is the latest and the latest save ends after every earlier one.
    private readonly Lock _saves = new();
    private volatile bool _decodesInSoftware;
    private volatile PlayerUpDown _upDown;
    private volatile bool _timeLeft;
    private volatile int _passoutThreshold;
    private volatile int _resumeRewindSeconds;
    private volatile bool _discordStatus;
    private volatile bool _sleepTimerButton;
    private Task _latestSave = Task.CompletedTask;

    public JsonPlaybackDevicePreferences(string filePath, ILogger<JsonPlaybackDevicePreferences> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        var file = Load();
        _decodesInSoftware = file?.DecodesInSoftware == true;
        _upDown = file?.UpDown == VolumeValue ? PlayerUpDown.Volume : PlayerUpDown.ControlsAndMenus;
        _timeLeft = file?.TimeLeft == true;
        _passoutThreshold = file?.PassoutThreshold is { } episodes and >= 0 and <= AutoPlayStreak.MaxThreshold
            ? episodes
            : AutoPlayStreak.DefaultThreshold;
        _resumeRewindSeconds = file?.ResumeRewindSeconds is { } seconds and >= 0 and <= ResumeRewind.MaxSeconds && double.IsInteger(seconds)
            ? (int)seconds
            : ResumeRewind.DefaultSeconds;
        _discordStatus = file?.DiscordStatus == true;
        // Off only when turned off: a file from before the switch shows the button.
        _sleepTimerButton = file?.SleepTimerButton != false;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "playback-device.json");

    public bool DecodesInSoftware => _decodesInSoftware;

    public PlayerUpDown UpDown => _upDown;

    public bool TimeLeft => _timeLeft;

    public int PassoutThreshold => _passoutThreshold;

    public int ResumeRewindSeconds => _resumeRewindSeconds;

    public bool DiscordStatus => _discordStatus;

    public event EventHandler? DiscordStatusChanged;

    public bool SleepTimerButton => _sleepTimerButton;

    public event EventHandler<bool>? SleepTimerButtonChanged;

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

    public Task SetUpDownAsync(PlayerUpDown upDown)
    {
        _upDown = upDown;
        return QueueSave();
    }

    public Task SetTimeLeftAsync(bool shown)
    {
        _timeLeft = shown;
        return QueueSave();
    }

    public Task SetPassoutThresholdAsync(int episodes)
    {
        _passoutThreshold = episodes;
        return QueueSave();
    }

    public Task SetResumeRewindSecondsAsync(int seconds)
    {
        _resumeRewindSeconds = Math.Clamp(seconds, 0, ResumeRewind.MaxSeconds);
        return QueueSave();
    }

    public Task SetDiscordStatusAsync(bool enabled)
    {
        bool changed = enabled != _discordStatus;
        _discordStatus = enabled;
        var save = QueueSave();
        if (changed)
        {
            DiscordStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        return save;
    }

    public Task SetSleepTimerButtonAsync(bool shown)
    {
        bool changed = shown != _sleepTimerButton;
        _sleepTimerButton = shown;
        var save = QueueSave();
        if (changed)
        {
            SleepTimerButtonChanged?.Invoke(this, shown);
        }
        return save;
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
        // Its own temporary file: a second Granary on the same data folder saving at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                var file = new PlaybackDeviceFile(_decodesInSoftware, _upDown == PlayerUpDown.Volume ? VolumeValue : ControlsValue, _passoutThreshold)
                {
                    TimeLeft = _timeLeft,
                    ResumeRewindSeconds = _resumeRewindSeconds,
                    DiscordStatus = _discordStatus,
                    SleepTimerButton = _sleepTimerButton,
                };
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
    }
}

/// <summary><c>playback-device.json</c>.</summary>
internal sealed record PlaybackDeviceFile(bool? DecodesInSoftware, string? UpDown, int? PassoutThreshold)
{
    public bool? TimeLeft { get; init; }

    // Missing from an older file: a resume then lands on the saved point, the default. Read leniently, so that 7.5,
    // "seven" or a list typed into the file only loses this value, not the file's other choices.
    [JsonConverter(typeof(AnyNumberConverter))]
    public double? ResumeRewindSeconds { get; init; }

    public bool? DiscordStatus { get; init; }

    public bool? SleepTimerButton { get; init; }
}

/// <summary>A JSON number as itself; any other value, an object or a list included, as none.</summary>
internal sealed class AnyNumberConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double number))
        {
            return number;
        }
        // Past the whole value, so the next member reads as it should.
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is { } number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(PlaybackDeviceFile))]
internal sealed partial class PlaybackDeviceJsonContext : JsonSerializerContext;
