using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Texts;

/// <summary>
/// The profile's date and time formats kept on this PC (<c>date-time-format.json</c>), as the web mirrors them in its
/// storage (hooks/useDateTimeFormat.tsx): the next start writes its dates in them before it knows who is watching, until
/// the profile's are read.
/// </summary>
/// <remarks>A file that cannot be read gives auto; one that cannot be written is logged, and the next keep tries again.</remarks>
public sealed class DateTimeFormatFile
{
    private readonly string _filePath;
    private readonly ILogger<DateTimeFormatFile> _logger;
    // Saves one at a time, each the value last asked for when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile DateTimePreferences _kept;
    private volatile DateTimePreferences _wanted;

    public DateTimeFormatFile(string filePath, ILogger<DateTimeFormatFile> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        AtStart = _kept = _wanted = Load();
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "date-time-format.json");

    /// <summary>What the file held as Granary started.</summary>
    public DateTimePreferences AtStart { get; }

    /// <summary>What the file holds.</summary>
    public DateTimePreferences Kept => _kept;

    /// <summary>Keeps <paramref name="preferences"/> for the next start. Never fails: a save that does not work is logged.</summary>
    public async Task KeepAsync(DateTimePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _wanted = preferences;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var wanted = _wanted;
            if (wanted != _kept)
            {
                await Task.Run(() => Write(wanted)).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Write(DateTimePreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var data = new DateTimeFormatFileData(preferences.DateFormat, preferences.TimeFormat);
            SafeFile.WriteWhole(_filePath, temp =>
                File.WriteAllText(temp, JsonSerializer.Serialize(data, DateTimeFormatJsonContext.Default.DateTimeFormatFileData)));
            _kept = preferences;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The date and time formats for the next start could not be saved");
        }
    }

    private DateTimePreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return DateTimePreferences.Default;
            }
            var data = JsonSerializer.Deserialize(File.ReadAllText(_filePath), DateTimeFormatJsonContext.Default.DateTimeFormatFileData);
            return DateTimePreferences.Parse(data?.DateFormat, data?.TimeFormat);
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: in the format locale's own dates.
            _logger.LogWarning(ex, "The date and time formats kept for this start could not be read; auto applies");
            return DateTimePreferences.Default;
        }
    }
}

/// <summary><c>date-time-format.json</c>: the profile's <c>ui.date_format</c> and <c>ui.time_format</c> as last read or chosen.</summary>
internal sealed record DateTimeFormatFileData(string? DateFormat, string? TimeFormat);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(DateTimeFormatFileData))]
internal sealed partial class DateTimeFormatJsonContext : JsonSerializerContext;
