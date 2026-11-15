using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Servers;

/// <summary>The news that the server answers again reaches every listener, and what one of them does wrong stays its own.</summary>
public sealed class ServerRecoveryTests
{
    [Fact]
    public void AListenerThatFails_ReachesNeitherTheReporterNorTheOtherListeners()
    {
        var logs = new LogSink();
        var recovery = new ServerRecovery(logs.For<ServerRecovery>());
        int heard = 0;
        recovery.Recovered += (_, _) => throw new InvalidOperationException("A listener went wrong.");
        recovery.Recovered += (_, _) => heard++;

        recovery.Report("Retry");

        Assert.Equal(1, heard);
        Assert.Contains(logs.Lines, line => line.Contains("A listener of the server's recovery failed (InvalidOperationException)", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("A listener went wrong.", StringComparison.Ordinal));
    }
}
