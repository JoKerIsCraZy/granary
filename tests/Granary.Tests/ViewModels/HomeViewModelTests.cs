using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels;
using Granary.Tests.Auth;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

public sealed class HomeViewModelTests
{
    private readonly AuthPipeline _pipeline = new();

    [Fact]
    public async Task Load_ShowsServerUserAndProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        await session.SelectProfileAsync("7", null, ct);
        var home = new HomeViewModel(session, new ProfilesApi(_pipeline.Http), _pipeline.Logs.For<HomeViewModel>());

        await home.LoadAsync(ct);

        Assert.Equal("Test server", home.ServerName);
        Assert.Equal("alex", home.UserName);
        Assert.Equal("Alex", home.ProfileName);
    }

    [Fact]
    public async Task Load_KeepsGoingWhenProfilesFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        await session.SelectProfileAsync("7", null, ct);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/profiles" ? FakeSiloServer.Problem(503, "dependency_unavailable") : null;
        var home = new HomeViewModel(session, new ProfilesApi(_pipeline.Http), _pipeline.Logs.For<HomeViewModel>());

        await home.LoadAsync(ct);

        Assert.Equal("alex", home.UserName);
        Assert.Equal("", home.ProfileName);
    }

    [Fact]
    public async Task SwitchProfileAndSignOut_ChangeTheSession()
    {
        var session = new FakeSessionManager();
        var home = new HomeViewModel(session, new ProfilesApi(_pipeline.Http), _pipeline.Logs.For<HomeViewModel>());

        await home.SwitchProfileCommand.ExecuteAsync(null);
        await home.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "clear-profile", "sign-out:active" }, session.Calls);
    }
}
