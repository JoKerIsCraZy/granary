using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    private static readonly ServerEntry Server = new("srv1", new Uri("https://silo.test"), "Living room", null, default, default, null);
    private readonly FakeSessionManager _session = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly LogSink _logs = new();

    private ShellViewModel Create() => new(_session, _navigation, new ImmediateDispatcher(), _logs.For<ShellViewModel>());

    private void Become(SessionState state, string? profileId = null)
    {
        _session.State = state;
        _session.ActiveServer = state == SessionState.NoServer ? null : Server;
        _session.ProfileId = profileId;
        _session.RaiseChanged();
    }

    [Theory]
    [InlineData(SessionState.NoServer, AppRoute.AddServer)]
    [InlineData(SessionState.PasswordChangeRequired, AppRoute.ChangePassword)]
    [InlineData(SessionState.ProfileRequired, AppRoute.ProfilePicker)]
    public async Task Start_GoesWhereTheRestoredStateRequires(SessionState state, AppRoute expected)
    {
        _session.State = state;
        _session.ActiveServer = state == SessionState.NoServer ? null : Server;

        await Create().StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("initialize", Assert.Single(_session.Calls));
        Assert.Equal(expected, Assert.Single(_navigation.Navigations).Route);
    }

    [Fact]
    public async Task SignedOut_OpensSignInForTheActiveServer()
    {
        var shell = Create();
        await shell.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        Become(SessionState.SignedOut);

        Assert.Equal((AppRoute.SignIn, (object?)"srv1"), _navigation.Navigations[^1]);
        Assert.False(shell.IsSignedIn);
    }

    [Fact]
    public async Task Ready_GoesHomeOnceAndShowsTheNavigation()
    {
        var shell = Create();
        await shell.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        Become(SessionState.Ready, "7");
        Become(SessionState.Ready, "7");

        Assert.Single(_navigation.Navigations, r => r.Route == AppRoute.Home);
        Assert.True(shell.IsSignedIn);
    }

    [Fact]
    public async Task AnotherProfile_GoesHomeAgain()
    {
        await Create().StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        Become(SessionState.Ready, "7");

        Become(SessionState.ProfileRequired);
        Become(SessionState.Ready, "8");

        Assert.Equal(new[] { AppRoute.AddServer, AppRoute.Home, AppRoute.ProfilePicker, AppRoute.Home }, _navigation.Navigations.Select(r => r.Route));
    }

    [Fact]
    public async Task InitialRoute_WinsOverTheSessionState()
    {
        _session.State = SessionState.Ready;
        _session.ActiveServer = Server;
        _session.ProfileId = "7";
        var shell = Create();

        await shell.StartAsync(AppRoute.Player, "movie.mkv", TestContext.Current.CancellationToken);
        _session.RaiseChanged();

        Assert.Equal((AppRoute.Player, (object?)"movie.mkv"), Assert.Single(_navigation.Navigations));
        Assert.True(shell.IsSignedIn);
    }

    [Fact]
    public async Task FailedRestore_IsLoggedAndStillRoutes()
    {
        _session.FailWith = new IOException("disk full");

        await Create().StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AppRoute.AddServer, Assert.Single(_navigation.Navigations).Route);
        Assert.Contains(_logs.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));
    }
}
