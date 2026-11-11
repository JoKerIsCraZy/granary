using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// <c>window.json</c> in the data directory: where the main window was as Granary closed (<see cref="WindowPlace"/>), this
/// PC's alone. Read once at start; written off the UI thread as the window settles, so a crash or Windows shutting down
/// keeps it too, and once more as the window closes, which is the last word. A place the file holds already is not written
/// again. No file, or one that cannot be read, opens the window where Windows places it, as before Granary kept it. One
/// that cannot be written is logged, and the next start opens where the file last said.
/// </summary>
public sealed class WindowPlaceFile
{
    // Windows refuses to replace a file while anything has it open, a virus scanner's read of the last write included; that
    // ends in a moment.
    private const int MoveTries = 4;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(40);

    // Further than any desktop reaches, or scales Windows does not have (100 % to 500 %, with room on both sides): a file
    // edited or damaged into such numbers says no place, and none such is written.
    private const int MaxPixels = 1 << 16;
    private const double MinScale = 0.5;
    private const double MaxScale = 5;

    // A window's place is a few lines; a larger file is something else, or damaged, and is not read.
    private const long MaxFileBytes = 64 * 1024;

    // A save's temporary file is written and moved over within moments; one older than this was left by a Granary that
    // ended mid-save.
    private static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(1);

    private readonly string _filePath;
    private readonly ILogger<WindowPlaceFile> _logger;
    private readonly TimeProvider _time;
    // Writes one at a time, and keeps the place the file holds. Only the writes take it: asking for a save never waits.
    private readonly Lock _gate = new();
    // The place a save as the window settled is to write, until it is written or a later save takes its place; swapped,
    // never locked.
    private WindowPlace? _pending;
    // The place the file holds, as read or written here.
    private WindowPlace? _written;
    // The save as the window closed has begun: the settle saves still to come are too late.
    private volatile bool _isClosed;

    public WindowPlaceFile(string filePath, ILogger<WindowPlaceFile> logger, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "window.json");

    /// <summary>For tests: runs before a write that the file being open got in the way of tries again.</summary>
    internal Action? BeforeMoveRetry { get; set; }

    /// <summary>
    /// How long the save as the window closes waits for a write in flight, as closing waits for the other saves; a write
    /// stalled on a slow or network data folder does not keep the window from closing.
    /// </summary>
    internal TimeSpan CloseWait { get; set; } = AppUpdatesViewModel.SaveWaitAtExit;

    /// <summary>The place the window was closed at; none the first time, or when the file cannot be read. Never throws.</summary>
    public WindowPlace? Load()
    {
        RemoveOrphans();
        try
        {
            var info = new FileInfo(_filePath);
            if (!info.Exists)
            {
                return null;
            }
            if (info.Length > MaxFileBytes)
            {
                _logger.LogWarning("The window's place file is {Length} bytes, too large to be one; Windows places the window", info.Length);
                return null;
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), WindowJsonContext.Default.WindowFile);
            if (ToPlace(file) is { } place)
            {
                lock (_gate)
                {
                    _written = place;
                }
                return place;
            }
            _logger.LogWarning("The window's place names no usable place and size; Windows places the window");
            return null;
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts, where Windows places it.
            _logger.LogWarning(ex, "The window's place could not be read; Windows places the window");
            return null;
        }
    }

    /// <summary>
    /// Saves the place off the caller's thread, as the window settles, without waiting for a write in flight. A later save,
    /// or the one as the window closes, takes its place if it has not been written yet; after that one it writes nothing.
    /// Never fails: a save that does not work is logged.
    /// </summary>
    public Task SaveAsync(WindowPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (_isClosed)
        {
            return Task.CompletedTask;
        }
        Interlocked.Exchange(ref _pending, place);
        return Task.Run(WritePending);
    }

    /// <summary>
    /// Saves the place to open at next time, as the window closes: after a write in flight, waited for at most
    /// <see cref="CloseWait"/>, in place of one still waiting, and as the last word. Never throws: a save that does not
    /// work is logged, and false.
    /// </summary>
    public bool Save(WindowPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        _isClosed = true;
        Interlocked.Exchange(ref _pending, null);
        if (!_gate.TryEnter(CloseWait))
        {
            _logger.LogWarning("The window's place was not saved as Granary was closing: the write before it took too long");
            return false;
        }
        try
        {
            return Write(place);
        }
        finally
        {
            _gate.Exit();
        }
    }

    private void WritePending()
    {
        lock (_gate)
        {
            // Taken under the gate, so a settle save never writes after the close's; checked there for one asked for as
            // the close began.
            if (Interlocked.Exchange(ref _pending, null) is { } place && !_isClosed)
            {
                Write(place);
            }
        }
    }

    // Under the gate.
    private bool Write(WindowPlace place)
    {
        if (!IsUsable(place))
        {
            // The next start would not read it, and would open where Windows places the window: the file keeps what it has.
            _logger.LogWarning("The window's place is not one the next start could use; it is not saved");
            return false;
        }
        if (place == _written)
        {
            return true;
        }
        // Its own temporary file: a second Granary on the same data folder closing at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bounds = place.Bounds;
            var file = new WindowFile(bounds.X, bounds.Y, bounds.Width, bounds.Height, place.Scale, place.IsMaximized)
            {
                FullScreen = place.IsFullScreen,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, file, WindowJsonContext.Default.WindowFile);
            }
            MoveOver(temp);
            _written = place;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The window's place could not be saved");
            TryDelete(temp);
            return false;
        }
    }

    private static WindowPlace? ToPlace(WindowFile? file)
    {
        if (file is not { X: { } x, Y: { } y, Width: { } width, Height: { } height, Scale: { } scale })
        {
            return null;
        }
        var place = new WindowPlace(new WindowBounds(x, y, width, height), scale, file.Maximized ?? false)
        {
            IsFullScreen = file.FullScreen == true,
        };
        return IsUsable(place) ? place : null;
    }

    // One check for what is read and what is written.
    private static bool IsUsable(WindowPlace place)
    {
        var bounds = place.Bounds;
        return bounds.X is >= -MaxPixels and <= MaxPixels && bounds.Y is >= -MaxPixels and <= MaxPixels
            && bounds.Width is > 0 and <= MaxPixels && bounds.Height is > 0 and <= MaxPixels
            && place.Scale is >= MinScale and <= MaxScale;
    }

    private void MoveOver(string temp)
    {
        for (int tries = 1; ; tries++)
        {
            try
            {
                File.Move(temp, _filePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && tries < MoveTries)
            {
                BeforeMoveRetry?.Invoke();
                Thread.Sleep(MoveRetryDelay);
            }
        }
    }

    // Temporary files of saves that never ended, as a Granary killed mid-save leaves them; never the file itself, nor one
    // another Granary on the same data folder is writing now.
    private void RemoveOrphans()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }
            string prefix = Path.GetFileName(_filePath) + ".";
            var before = _time.GetUtcNow().UtcDateTime - OrphanAge;
            foreach (string path in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
            {
                if (IsTemporaryName(Path.GetFileName(path), prefix) && File.GetLastWriteTimeUtc(path) < before)
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Half-written window place files were not looked for");
        }
    }

    // "window.json.<32 hex digits>.tmp", as Write names them.
    private static bool IsTemporaryName(string name, string prefix)
    {
        const string Suffix = ".tmp";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var id = name.AsSpan(prefix.Length, name.Length - prefix.Length - Suffix.Length);
        return id.Length == 32 && Guid.TryParseExact(id, "N", out _);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A half-written window place file stays behind");
        }
    }
}

/// <summary>
/// <c>window.json</c>: the window's place and size in pixels, the scale of their display, whether it was maximized, and
/// whether it was full screen.
/// </summary>
internal sealed record WindowFile(int? X, int? Y, int? Width, int? Height, double? Scale, bool? Maximized)
{
    // Read leniently: missing, or anything but true or false, opens as the window, and the place still holds.
    [JsonConverter(typeof(Playback.AnyFlagConverter))]
    public bool? FullScreen { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(WindowFile))]
internal sealed partial class WindowJsonContext : JsonSerializerContext;
