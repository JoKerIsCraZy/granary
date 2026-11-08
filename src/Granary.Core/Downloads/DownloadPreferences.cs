using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.Api.Downloads;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>This PC's download settings (Settings → Downloads): about its drives and connections, so they stay with the PC.</summary>
public interface IDownloadPreferences
{
    /// <summary>Where new downloads go; downloads already there stay where they are.</summary>
    string Folder { get; }

    /// <summary>Downloads run on a metered connection, such as a phone's hotspot; off by default.</summary>
    bool DownloadsOnMetered { get; }

    /// <summary>The preset the download dialog starts on, when the server offers it; <c>original</c> by default.</summary>
    string DefaultQuality { get; }

    /// <summary>Raised on the caller's thread after any of them changed.</summary>
    event EventHandler? Changed;

    /// <summary>The latest save: done once it is on disk, or failed and logged.</summary>
    Task LatestSave { get; }

    Task SetFolderAsync(string folder);

    Task SetDownloadsOnMeteredAsync(bool allowed);

    Task SetDefaultQualityAsync(string quality);
}

/// <summary>
/// <see cref="IDownloadPreferences"/> in <c>download-settings.json</c> in the data directory
/// (<c>{ "folder": "D:\\Granary", "downloads_on_metered": true, "default_quality": "10mbps" }</c>). Without the file, or with one
/// that cannot be read, downloads go to <see cref="DownloadPaths.DefaultRoot"/>, wait on metered connections and start on
/// Original; a value it does not know is the default. One that cannot be written is logged, and the choice still holds for this
/// run. Files are written off the caller's thread, which is the UI's.
/// </summary>
public sealed class JsonDownloadPreferences : IDownloadPreferences
{
    private readonly string _filePath;
    private readonly ILogger<JsonDownloadPreferences> _logger;

    // Each save runs after the one before it on the thread pool, writing the choices as they are when its turn comes, so
    // the last one written is the latest and the latest save ends after every earlier one.
    private readonly Lock _saves = new();
    private volatile string _folder;
    private volatile bool _downloadsOnMetered;
    private volatile string _defaultQuality;
    private Task _latestSave = Task.CompletedTask;

    public JsonDownloadPreferences(string filePath, ILogger<JsonDownloadPreferences> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        var file = Load();
        _folder = FolderOf(file?.Folder);
        _downloadsOnMetered = file?.DownloadsOnMetered == true;
        _defaultQuality = file?.DefaultQuality is { } quality && DownloadQualities.Ladder.Contains(quality, StringComparer.Ordinal)
            ? quality
            : DownloadQualities.Original;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "download-settings.json");

    public string Folder => _folder;

    public bool DownloadsOnMetered => _downloadsOnMetered;

    public string DefaultQuality => _defaultQuality;

    public event EventHandler? Changed;

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

    /// <exception cref="ArgumentException">
    /// <paramref name="folder"/> is not a full path to a folder: a relative path, a drive or a share's root (Granary's folders
    /// and the look for them belong in a folder of their own), or a path Windows cannot have, such as <c>C:\a:b</c>.
    /// </exception>
    public Task SetFolderAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = FullFolder(folder) ?? throw new ArgumentException("The downloads folder must be a full path to a folder below a drive's root.",
            nameof(folder));
        return SaveAndRaise();
    }

    // The full path of a folder downloads may go to, or null: not a relative path, a drive or share root, or a path with a
    // colon past its drive (another stream of a file). Settings → Downloads asks it before it writes to a folder.
    internal static string? FullFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return null;
        }
        try
        {
            string full = Path.GetFullPath(folder);
            string root = Path.GetPathRoot(full) ?? "";
            bool isRoot = string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            return isRoot || full[root.Length..].Contains(':', StringComparison.Ordinal) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public Task SetDownloadsOnMeteredAsync(bool allowed)
    {
        _downloadsOnMetered = allowed;
        return SaveAndRaise();
    }

    /// <exception cref="ArgumentException"><paramref name="quality"/> is not one of <see cref="DownloadQualities.Ladder"/>.</exception>
    public Task SetDefaultQualityAsync(string quality)
    {
        if (!DownloadQualities.Ladder.Contains(quality, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{quality}' is not a download quality.", nameof(quality));
        }
        _defaultQuality = quality;
        return SaveAndRaise();
    }

    private Task SaveAndRaise()
    {
        var save = QueueSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return save;
    }

    // Save never throws, so the chain never breaks.
    private Task QueueSave()
    {
        lock (_saves)
        {
            return _latestSave = _latestSave.ContinueWith(_ => Save(), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }
    }

    private DownloadSettingsFile? Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_filePath), DownloadSettingsJsonContext.Default.DownloadSettingsFile)
                : null;
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts with the defaults.
            _logger.LogWarning("This PC's download settings could not be read ({Failure}); the defaults apply", DownloadLog.Failure(ex));
            return null;
        }
    }

    // The file's folder when it is a full path that can be one; anything else is the default.
    private string FolderOf(string? folder)
    {
        if (folder is null)
        {
            return DownloadPaths.DefaultRoot;
        }
        if (FullFolder(folder) is { } full)
        {
            return full;
        }
        _logger.LogWarning("The downloads folder in this PC's settings is no folder downloads may go to; the default applies");
        return DownloadPaths.DefaultRoot;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var file = new DownloadSettingsFile(_folder, _downloadsOnMetered, _defaultQuality);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(file, DownloadSettingsJsonContext.Default.DownloadSettingsFile);
            SafeFile.WriteWhole(_filePath, temp => File.WriteAllBytes(temp, json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never the setting's to fail: the choice holds for this run.
            _logger.LogWarning("This PC's download settings could not be saved ({Failure})", DownloadLog.Failure(ex));
        }
    }
}

/// <summary><c>download-settings.json</c>.</summary>
internal sealed record DownloadSettingsFile(string? Folder, bool? DownloadsOnMetered, string? DefaultQuality);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(DownloadSettingsFile))]
internal sealed partial class DownloadSettingsJsonContext : JsonSerializerContext;
