using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// <c>window.json</c> in the data directory: where the main window was as Granary closed (<see cref="WindowPlace"/>), this
/// PC's alone. Read once at start; written off the UI thread as the window settles, so a crash or Windows shutting down
/// keeps it too, and once more as the window closes. A place the file holds already is not written again. No file, or one
/// that cannot be read, opens the window where Windows places it, as before Granary kept it. One that cannot be written is
/// logged, and the next start opens where the file last said.
/// </summary>
public sealed class WindowPlaceFile
{
    // Windows refuses to replace a file while anything has it open, a virus scanner's read of the last write included; that
    // ends in a moment.
    private const int MoveTries = 4;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(40);

    // Further than any desktop reaches: a file edited or damaged into such numbers says no place.
    private const int MaxPixels = 1 << 16;
    private const double MaxScale = 10;

    private readonly string _filePath;
    private readonly ILogger<WindowPlaceFile> _logger;
    // Writes one at a time, and keeps the two below.
    private readonly Lock _gate = new();
    // The place a save as the window settled is to write, until it is written or a later save takes its place.
    private WindowPlace? _pending;
    // The place the file holds, as read or written here.
    private WindowPlace? _written;

    public WindowPlaceFile(string filePath, ILogger<WindowPlaceFile> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "window.json");

    /// <summary>For tests: runs before a write that the file being open got in the way of tries again.</summary>
    internal Action? BeforeMoveRetry { get; set; }

    /// <summary>The place the window was closed at; none the first time, or when the file cannot be read. Never throws.</summary>
    public WindowPlace? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
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
    /// Saves the place off the caller's thread, as the window settles. A later save, or the one as the window closes, takes
    /// its place if it has not been written yet. Never fails: a save that does not work is logged.
    /// </summary>
    public Task SaveAsync(WindowPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        lock (_gate)
        {
            _pending = place;
        }
        return Task.Run(WritePending);
    }

    /// <summary>
    /// Saves the place to open at next time, as the window closes: after a save in flight, and in place of one still waiting.
    /// Never throws: a save that does not work is logged, and false.
    /// </summary>
    public bool Save(WindowPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        lock (_gate)
        {
            _pending = null;
            return Write(place);
        }
    }

    private void WritePending()
    {
        lock (_gate)
        {
            if (_pending is { } place)
            {
                _pending = null;
                Write(place);
            }
        }
    }

    // Under the gate.
    private bool Write(WindowPlace place)
    {
        if (place == _written)
        {
            return true;
        }
        // Its own temporary file: a second Granary on the same data folder closing at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bounds = place.Bounds;
            var file = new WindowFile(bounds.X, bounds.Y, bounds.Width, bounds.Height, place.Scale, place.IsMaximized);
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
        bool usable = Math.Abs(x) <= MaxPixels && Math.Abs(y) <= MaxPixels
            && width is > 0 and <= MaxPixels && height is > 0 and <= MaxPixels
            && double.IsFinite(scale) && scale is > 0 and <= MaxScale;
        return usable ? new WindowPlace(new WindowBounds(x, y, width, height), scale, file.Maximized ?? false) : null;
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

/// <summary><c>window.json</c>: the window's place and size in pixels, the scale of their display, and whether it was maximized.</summary>
internal sealed record WindowFile(int? X, int? Y, int? Width, int? Height, double? Scale, bool? Maximized);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(WindowFile))]
internal sealed partial class WindowJsonContext : JsonSerializerContext;
