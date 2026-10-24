using Granary.Core.Api.Discovery;
using Granary.Core.Servers;
using Granary.Core.ViewModels;

namespace Granary.Tests.ViewModels;

public sealed class AddServerViewModelTests
{
    private sealed class ScriptedProbe(Func<Uri, ServerProbeResult> answer) : IServerProbe
    {
        public List<Uri> Probed { get; } = [];

        public Task<ServerProbeResult> ProbeAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            Probed.Add(baseUri);
            return Task.FromResult(answer(baseUri));
        }
    }

    private static readonly SystemInfo V2 = new(2, "digest", "dev", null);

    [Fact]
    public async Task Connect_TriesCandidatesUntilAV2ServerAnswers()
    {
        var probe = new ScriptedProbe(uri => uri.Scheme == "https"
            ? new ServerProbeResult.NoVerdict(uri, "network: ConnectionError")
            : new ServerProbeResult.Compatible(uri, V2));
        var viewModel = new AddServerViewModel(probe) { Address = "nas.local" };

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(["https://nas.local", "http://nas.local"], probe.Probed.Select(u => u.OriginalString));
        Assert.Equal("http://nas.local", viewModel.ConnectedServer?.OriginalString);
        Assert.Contains("Found", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_StopsAtAServerThatMustBeUpdated()
    {
        var probe = new ScriptedProbe(uri => new ServerProbeResult.UpdateRequired(uri));
        var viewModel = new AddServerViewModel(probe) { Address = "old.example.org" };

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Single(probe.Probed);
        Assert.Null(viewModel.ConnectedServer);
        Assert.Contains("too old", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_ReportsWhenNoCandidateAnswers()
    {
        var probe = new ScriptedProbe(uri => new ServerProbeResult.NoVerdict(uri, "timeout"));
        var viewModel = new AddServerViewModel(probe) { Address = "nobody.example" };

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(3, probe.Probed.Count);
        Assert.Null(viewModel.ConnectedServer);
        Assert.Contains("timeout", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_DoesNotProbeAnInvalidAddress()
    {
        var probe = new ScriptedProbe(uri => new ServerProbeResult.Compatible(uri, V2));
        var viewModel = new AddServerViewModel(probe) { Address = "ftp://nope" };

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Empty(probe.Probed);
        Assert.Contains("not a server address", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectCommand_IsDisabledWithoutAnAddress()
    {
        var viewModel = new AddServerViewModel(new ScriptedProbe(uri => new ServerProbeResult.UpdateRequired(uri)));

        Assert.False(viewModel.ConnectCommand.CanExecute(null));
        viewModel.Address = "nas";
        Assert.True(viewModel.ConnectCommand.CanExecute(null));
    }
}
