using System.Globalization;
using Granary.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Granary.Core;

namespace Granary.App.Logging;

/// <summary>
/// Writes a daily log file under %LOCALAPPDATA%\Granary\logs. Every line passes through
/// <see cref="Redact"/>, so tokens and credentials never reach the disk even if a caller logs them.
/// </summary>
internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;

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
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_writer is null || today != _day)
            {
                _writer?.Dispose();
                Directory.CreateDirectory(directory);
                _writer = new StreamWriter(Path.Combine(directory, $"granary-{today:yyyyMMdd}.log"), append: true) { AutoFlush = true };
                _day = today;
            }
            _writer.WriteLine(line);
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
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
