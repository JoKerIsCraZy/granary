using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Granary.Tests.Fakes;

/// <summary>Collects every log line, exception text included, so tests can check what would reach the log file.</summary>
internal sealed class LogSink
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger<T> For<T>() => new CapturingLogger<T>(this);

    public string All => string.Join(Environment.NewLine, Lines);

    /// <summary>A factory whose loggers write here, under the category's last name segment.</summary>
    public ILoggerFactory AsFactory() => new Factory(this);

    private sealed class Factory(LogSink sink) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CategoryLogger(sink, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CategoryLogger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}

internal sealed class CapturingLogger<T>(LogSink sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        sink.Lines.Enqueue($"{logLevel} {typeof(T).Name}: {formatter(state, exception)} {exception}");
}
