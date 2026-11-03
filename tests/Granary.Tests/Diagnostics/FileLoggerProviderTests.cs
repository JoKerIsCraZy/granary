using Granary.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Granary.Tests.Diagnostics;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-log-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TwoInstances_WritingTheSameDaysLog_BothKeepTheirLines()
    {
        var first = new FileLoggerProvider(_directory);
        var second = new FileLoggerProvider(_directory);

        first.CreateLogger("First").LogInformation("from the first");
        second.CreateLogger("Second").LogInformation("from the second");
        first.Dispose();
        second.Dispose();

        string text = ReadAll();
        Assert.Contains("from the first", text, StringComparison.Ordinal);
        Assert.Contains("from the second", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogFileThatCannotBeOpened_DropsTheLine_AndNeverThrows()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"granary-{DateTime.Now:yyyyMMdd}.log");
        using var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var provider = new FileLoggerProvider(_directory);

        var thrown = Record.Exception(() => provider.CreateLogger("Locked").LogWarning("dropped"));

        Assert.Null(thrown);
    }

    [Fact]
    public void AMessageWithLineBreaks_StaysOneLine_SoAServerCannotWriteLinesOfItsOwn()
    {
        using (var provider = new FileLoggerProvider(_directory))
        {
            provider.CreateLogger("Playback").LogInformation("The server's reason: {Reason}", "x\r\n12:00:00.000 Error Forged: line");
        }

        string[] lines = ReadAll().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
        Assert.EndsWith("The server's reason: x\\r\\n12:00:00.000 Error Forged: line", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionsStackTrace_KeepsItsOwnLines()
    {
        using (var provider = new FileLoggerProvider(_directory))
        {
            provider.CreateLogger("Playback").LogError(new InvalidOperationException("first" + Environment.NewLine + "second"), "Failed");
        }

        Assert.Contains("first" + Environment.NewLine + "second", ReadAll(), StringComparison.Ordinal);
    }

    private string ReadAll() => string.Concat(Directory.GetFiles(_directory, "*.log").Select(path =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }));
}
