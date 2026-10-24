using Microsoft.Extensions.Logging;

namespace Granary.Tests.Profiles;

/// <summary>Keeps every formatted log line (and exception text) so tests can check that no secret leaked.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
}
