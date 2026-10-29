using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Diagnostics;

/// <summary>
/// Writes a daily log file under %LOCALAPPDATA%\Granary\logs. Every line passes through
/// <see cref="Redact"/>, so tokens and credentials never reach the disk even if a caller logs them.
/// </summary>
/// <remarks>
/// A log line never ends the app: the file is shared with any other Granary writing the same folder (a second
/// instance, a test run), and a line that still cannot be written is dropped, trying the file again a little later.
/// </remarks>
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    // How long a file that would not open is left alone before the next line tries it again.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;
    private DateTime _retryAt;

    public static string DefaultDirectory =>
        Path.Combine(AppPaths.DataDirectory, "logs");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private static string Redact(string text) => LogRedactor.Redact(text);

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff} {level,-11} {category}: {Redact(message)}");
        if (exception is not null)
        {
            line += Environment.NewLine + Redact(exception.ToString());
        }
        lock (_gate)
        {
            try
            {
                Writer()?.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk or a file taken away: drop the line and open the file again later.
                Close(retry: true);
            }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    // Today's file, shared for reading and writing, or null while one that would not open waits for its retry.
    private StreamWriter? Writer()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && today == _day)
        {
            return _writer;
        }
        Close(retry: false);
        if (DateTime.UtcNow < _retryAt)
        {
            return null;
        }
        try
        {
            Directory.CreateDirectory(directory);
            var stream = new FileStream(Path.Combine(directory, $"granary-{today:yyyyMMdd}.log"), FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream) { AutoFlush = true };
            _day = today;
            return _writer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _retryAt = DateTime.UtcNow + RetryAfter;
            return null;
        }
    }

    private void Close(bool retry)
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The last buffered bytes could not go out either; the file is let go all the same.
        }
        _writer = null;
        if (retry)
        {
            _retryAt = DateTime.UtcNow + RetryAfter;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Close(retry: false);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
