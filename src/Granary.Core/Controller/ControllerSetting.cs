using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Controller;

/// <summary>
/// Settings → Navigation &amp; Cards → Game controller: whether Granary takes a game controller's input. Off by default:
/// a pad or a wheel may be attached for other programs, and Granary reads none until the switch is on. Granary's own
/// switch: Silo's clients have no such switch.
/// </summary>
/// <remarks>
/// Kept for the whole PC, not a profile, in <c>controller.json</c> in the data directory (<c>{ "enabled": true }</c>). A
/// file that is missing or cannot be read leaves it off; one that cannot be written is logged, and the choice still holds
/// for this run. A change is told at once, on the caller's thread (the Settings switch's, the UI's), and written off it.
/// </remarks>
public sealed partial class ControllerSetting : ObservableObject
{
    private readonly string _filePath;
    private readonly ILogger<ControllerSetting> _logger;
    // Saves one at a time, each the choice as it is when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _isEnabled;

    public ControllerSetting(string filePath, ILogger<ControllerSetting> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        _isEnabled = Load();
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "controller.json");

    /// <summary>Granary takes a game controller's input.</summary>
    public bool IsEnabled => _isEnabled;

    /// <summary>The save in flight, for tests.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Whether the window reads the pads now: only with the switch on, a pad attached and the window in front, so a game
    /// played with the same pad behind it never moves Granary.
    /// </summary>
    public bool ReadsPads(bool isInFront, bool hasPads) => _isEnabled && isInFront && hasPads;

    /// <summary>Turns controller input on or off, and saves it. Never fails: a save that does not work is logged.</summary>
    public Task SetEnabledAsync(bool enabled)
    {
        if (enabled == _isEnabled)
        {
            return Task.CompletedTask;
        }
        _isEnabled = enabled;
        OnPropertyChanged(nameof(IsEnabled));
        return Saving = Task.Run(SaveAsync);
    }

    private bool Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                // Never chosen: off, the default.
                return false;
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), ControllerJsonContext.Default.ControllerFile);
            if (file?.Enabled is { } enabled)
            {
                return enabled;
            }
            _logger.LogWarning("The game controller's setting names no choice; controllers are off until they are switched on");
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts, and reads no controller.
            _logger.LogWarning(ex, "The game controller's setting could not be read; controllers are off until they are switched on");
        }
        return false;
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        // Its own temporary file: a second Granary on the same data folder saving at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new ControllerFile(_isEnabled), ControllerJsonContext.Default.ControllerFile)
                    .ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the switch's to fail: the choice holds for this run.
            _logger.LogWarning(ex, "The game controller's setting could not be saved");
            TryDelete(temp);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A half-written file of the game controller's setting stays behind");
        }
    }
}

/// <summary><c>controller.json</c>: whether Granary takes a game controller's input.</summary>
internal sealed record ControllerFile(bool? Enabled);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(ControllerFile))]
internal sealed partial class ControllerJsonContext : JsonSerializerContext;
