using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Updates;

/// <summary>
/// <see cref="IUpdatePreferences"/> in <c>updates.json</c> in the data directory (<c>{ "automatic": false }</c>). Without the
/// file updates are automatic. A file that is there but cannot be read (cut short by a crash or a full disk) may have said
/// off, so they are off until switched on: nothing installs without asking. One that cannot be written is logged, and the
/// choice still holds for this run. Files are written off the caller's thread, which is the UI's.
/// </summary>
public sealed class JsonUpdatePreferences : IUpdatePreferences
{
    private readonly string _filePath;
    private readonly ILogger<JsonUpdatePreferences> _logger;
    // Saves one at a time, each the choice as it is when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _automatic;

    public JsonUpdatePreferences(string filePath, ILogger<JsonUpdatePreferences> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        _automatic = Load();
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "updates.json");

    public bool IsAutomatic => _automatic;

    public Task SetAutomaticAsync(bool automatic)
    {
        _automatic = automatic;
        return Task.Run(SaveAsync);
    }

    private bool Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                // Never chosen: on, the default.
                return true;
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), UpdatesJsonContext.Default.UpdatesFile);
            if (file?.Automatic is { } automatic)
            {
                return automatic;
            }
            _logger.LogWarning("The updates file names no choice; automatic updates are off until they are switched on");
            return false;
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts; About says that automatic updates are off.
            _logger.LogWarning(ex, "Whether updates are automatic could not be read; they are off until they are switched on");
            return false;
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
                await JsonSerializer.SerializeAsync(stream, new UpdatesFile(_automatic), UpdatesJsonContext.Default.UpdatesFile).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the switch's to fail: the choice holds for this run.
            _logger.LogWarning(ex, "Whether updates are automatic could not be saved");
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
            _logger.LogDebug(ex, "A half-written updates file stays behind");
        }
    }
}

/// <summary><c>updates.json</c>: whether updates are automatic.</summary>
internal sealed record UpdatesFile(bool? Automatic);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(UpdatesFile))]
internal sealed partial class UpdatesJsonContext : JsonSerializerContext;
