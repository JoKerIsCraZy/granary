using System.Security.Cryptography;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

public sealed class ServersViewModelTests
{
    private readonly InMemoryServerRegistry _registry = new();
    private readonly FakeSessionManager _session = new();
    private readonly ServersRecordingNavigation _navigation = new();

    private async Task<(ServerEntry Nas, ServerEntry Lan)> SaveTwoServersAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var nas = await _registry.AddOrUpdateAsync(new Uri("https://nas.example"), "Living room", null, ct);
        var lan = await _registry.AddOrUpdateAsync(new Uri("http://192.168.1.10:8090"), "Basement", null, ct);
        await _registry.SetActiveAsync(nas.Id, ct);
        return (nas, lan);
    }

    private ServersViewModel Create() => new(_registry, _session, _navigation, new ImmediateDispatcher());

    [Fact]
    public async Task Servers_ListsEveryServer_WithTheActiveOneMarked()
    {
        var (nas, lan) = await SaveTwoServersAsync();

        using var viewModel = Create();

        Assert.Equal(
            [
                new ServerItemViewModel(nas.Id, "Living room", "https://nas.example", true, null),
                new ServerItemViewModel(lan.Id, "Basement", "http://192.168.1.10:8090", false, null),
            ],
            viewModel.Servers);
        Assert.False(viewModel.HasNoServers);
        Assert.False(viewModel.Servers[0].CanSwitch);
        Assert.True(viewModel.Servers[1].CanSwitch);
    }

    [Fact]
    public void Servers_IsEmpty_WithoutSavedServers()
    {
        using var viewModel = Create();

        Assert.Empty(viewModel.Servers);
        Assert.True(viewModel.HasNoServers);
    }

    [Fact]
    public async Task Servers_ShowTheSignedInAccount_OfTheActiveServer()
    {
        var (nas, _) = await SaveTwoServersAsync();
        using var viewModel = Create();

        _session.ActiveServer = nas;
        _session.State = SessionState.Ready;
        _session.Account = new Account("1", "elena", "", "user", [], true, false, null);
        _session.RaiseChanged();

        Assert.Equal("elena", viewModel.Servers[0].AccountName);
        Assert.Equal("Signed in as elena", viewModel.Servers[0].AccountText);
        Assert.Null(viewModel.Servers[1].AccountName);
    }

    [Fact]
    public async Task Servers_Refresh_WhenTheRegistryChanges()
    {
        var (_, lan) = await SaveTwoServersAsync();
        using var viewModel = Create();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await _registry.SetActiveAsync(lan.Id, TestContext.Current.CancellationToken);

        Assert.Contains(nameof(ServersViewModel.Servers), changed);
        Assert.Equal([false, true], viewModel.Servers.Select(s => s.IsActive));
    }

    [Fact]
    public async Task Switch_ActivatesTheServerThroughTheSession_WithoutNavigating()
    {
        var (_, lan) = await SaveTwoServersAsync();
        using var viewModel = Create();

        await viewModel.SwitchCommand.ExecuteAsync(viewModel.Servers[1]);

        Assert.Equal([$"activate:{lan.Id}"], _session.Calls);
        Assert.Empty(_navigation.Navigations);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SignOut_SignsOutThatServer_AndKeepsIt()
    {
        var (_, lan) = await SaveTwoServersAsync();
        using var viewModel = Create();

        await viewModel.SignOutCommand.ExecuteAsync(viewModel.Servers[1]);

        Assert.Equal([$"sign-out:{lan.Id}"], _session.Calls);
        Assert.Equal(2, _registry.Servers.Count);
        Assert.Contains("Signed out of Basement", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_SignsOutThenForgetsTheServer()
    {
        var (nas, lan) = await SaveTwoServersAsync();
        using var viewModel = Create();

        await viewModel.RemoveCommand.ExecuteAsync(viewModel.Servers[1]);

        Assert.Equal([$"sign-out:{lan.Id}"], _session.Calls);
        Assert.Null(_registry.Find(lan.Id));
        Assert.Equal([nas.Id], viewModel.Servers.Select(s => s.Id));
        Assert.Contains("Removed Basement", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_OfTheActiveServer_ActivatesTheMostRecentlyUsedRemainingOne()
    {
        var (nas, lan) = await SaveTwoServersAsync();
        using var viewModel = Create();

        await viewModel.RemoveCommand.ExecuteAsync(viewModel.Servers[0]);

        Assert.Equal([$"sign-out:{nas.Id}", $"activate:{lan.Id}"], _session.Calls);
        Assert.Equal(lan.Id, _registry.ActiveServerId);
        var remaining = Assert.Single(viewModel.Servers);
        Assert.True(remaining.IsActive);
    }

    [Fact]
    public async Task Remove_StillForgetsTheServer_WhenSignOutFails()
    {
        var (_, lan) = await SaveTwoServersAsync();
        using var viewModel = Create();
        _session.FailWith = new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");

        await viewModel.RemoveCommand.ExecuteAsync(viewModel.Servers[1]);

        Assert.Null(_registry.Find(lan.Id));
        Assert.Contains("Removed Basement", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("did not confirm the sign-out", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switch_ShowsAFriendlyStatus_WhenTheServerCannotBeReached()
    {
        await SaveTwoServersAsync();
        using var viewModel = Create();
        _session.FailWith = new HttpRequestException(HttpRequestError.ConnectionError, "Bearer secret-token-123 was refused");

        await viewModel.SwitchCommand.ExecuteAsync(viewModel.Servers[1]);

        Assert.Equal("Could not reach Basement. Check the connection and try again.", viewModel.Status);
        Assert.DoesNotContain("secret-token-123", viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Theory]
    [InlineData(503, ProblemCodes.DependencyUnavailable, "Basement has a problem right now. Try again later.")]
    [InlineData(429, ProblemCodes.RateLimited, "Basement is busy. Wait a moment and try again.")]
    [InlineData(401, ProblemCodes.SessionExpired, "Basement refused the request (session_expired).")]
    public async Task SignOut_ShowsAFriendlyStatus_WhenTheServerAnswersWithAnError(int status, string code, string expected)
    {
        await SaveTwoServersAsync();
        using var viewModel = Create();
        _session.FailWith = new SiloApiException(status, new ApiProblem($"{ApiProblem.TypeOrigin}{code}", null, status, "token abc", null, null));

        await viewModel.SignOutCommand.ExecuteAsync(viewModel.Servers[1]);

        Assert.Equal(expected, viewModel.Status);
        Assert.Equal(2, _registry.Servers.Count);
    }

    public static TheoryData<Exception, string> LocalAndProtocolFailures => new()
    {
        { new TaskCanceledException("timed out", new TimeoutException()), "Basement did not answer in time. Try again." },
        { new JsonException("'<' is an invalid start of a value."), "Basement sent an answer Granary does not understand. Check the server address." },
        { new IOException("disk full"), "Granary could not save its settings on this computer. Try again." },
        { new CryptographicException("key not valid"), "Granary could not read or save its sign-in data on this computer." },
    };

    [Theory]
    [MemberData(nameof(LocalAndProtocolFailures))]
    public async Task Switch_ShowsAFriendlyStatus_ForOtherExpectedFailures(Exception failure, string expected)
    {
        await SaveTwoServersAsync();
        using var viewModel = Create();
        _session.FailWith = failure;

        await viewModel.SwitchCommand.ExecuteAsync(viewModel.Servers[1]);

        Assert.Equal(expected, viewModel.Status);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Commands_IgnoreAMissingServer()
    {
        using var viewModel = Create();

        await viewModel.SwitchCommand.ExecuteAsync(null);
        await viewModel.SignOutCommand.ExecuteAsync(null);
        await viewModel.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(_session.Calls);
    }

    [Fact]
    public void AddServer_NavigatesToAddServer()
    {
        using var viewModel = Create();

        viewModel.AddServerCommand.Execute(null);

        Assert.Equal([(AppRoute.AddServer, (object?)null)], _navigation.Navigations);
    }

    [Fact]
    public async Task Dispose_StopsListeningForChanges()
    {
        var viewModel = Create();

        viewModel.Dispose();
        await _registry.AddOrUpdateAsync(new Uri("https://nas.example"), "Living room", null, TestContext.Current.CancellationToken);
        _session.RaiseChanged();

        Assert.Empty(viewModel.Servers);
    }
}
