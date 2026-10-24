using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

public sealed class SidebarViewModelTests
{
    private static readonly ServerEntry Server = new("srv1", new Uri("https://silo.test"), "JoKeR", null, default, default, null);
    private readonly FakeSessionManager _session = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly LogSink _logs = new();
    private readonly List<string> _requests = [];
    private Func<HttpResponseMessage> _profiles = () => FakeSiloServer.Json(200,
        """{"items":[{"id":"7","name":"Elena Moreau","is_primary":true,"has_pin":false,"is_child":false,"avatar_url":"/api/v2/artwork/a.webp?sig=x"}],"avatar_upload_enabled":false}""");

    private Func<HttpResponseMessage> _libraries = () => FakeSiloServer.Json(200,
        """{"items":[{"id":"2","name":"Serien","type":"series","sort_order":2},{"id":"1","name":"Filme","type":"movies","sort_order":1}]}""");

    private SidebarViewModel Create()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            _requests.Add(path);
            return Task.FromResult(path.EndsWith("/user/libraries", StringComparison.Ordinal) ? _libraries() : _profiles());
        });
        var http = new SiloHttp(new HttpClient(handler));
        return new SidebarViewModel(_session, new ProfilesApi(http), new CatalogApi(http), _navigation, new ImmediateDispatcher(),
            _logs.For<SidebarViewModel>());
    }

    private void Ready(string profileId = "7")
    {
        _session.State = SessionState.Ready;
        _session.ActiveServer = Server;
        _session.Account = new Account("1", "elena", "", "admin", [], true, false, null);
        _session.ProfileId = profileId;
    }

    [Fact]
    public async Task Ready_ShowsServerAndActingProfile()
    {
        Ready();
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("JoKeR", sidebar.ServerName);
        Assert.Equal("Elena Moreau", sidebar.ProfileName);
        Assert.Equal("EM", sidebar.ProfileInitials);
        Assert.Equal(new Uri("https://silo.test/api/v2/artwork/a.webp?sig=x"), sidebar.Profile!.AvatarUrl);
        Assert.Equal(new[] { "Filme", "Serien" }, sidebar.Libraries.Select(l => l.Name));
        Assert.Equal("movies", sidebar.Libraries[0].Type);
    }

    [Fact]
    public async Task FailedLibraries_LeaveTheListEmptyAndTryAgainLater()
    {
        Ready();
        _libraries = () => FakeSiloServer.Problem(503, ProblemCodes.DependencyUnavailable);
        using var sidebar = Create();
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(sidebar.Libraries);

        _libraries = () => FakeSiloServer.Json(200, """{"items":[{"id":"1","name":"Filme","type":"movies","sort_order":1}]}""");
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Filme", Assert.Single(sidebar.Libraries).Name);
    }

    [Fact]
    public async Task SameSession_IsNotLoadedTwice()
    {
        Ready();
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task ProfileChange_ReloadsThroughTheSessionEvent()
    {
        Ready();
        using var sidebar = Create();
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await Auth.AuthPipeline.Eventually(() => _requests.Count == 4);

        Assert.Null(sidebar.Profile);
        Assert.Equal("elena", sidebar.ProfileName);
    }

    [Fact]
    public async Task NotReady_ShowsNoProfileAndAsksNothing()
    {
        _session.State = SessionState.ProfileRequired;
        _session.ActiveServer = Server;
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("JoKeR", sidebar.ServerName);
        Assert.Null(sidebar.Profile);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task FailedProfileList_FallsBackToTheAccountName()
    {
        Ready();
        _profiles = () => FakeSiloServer.Problem(503, ProblemCodes.DependencyUnavailable);
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("elena", sidebar.ProfileName);
        Assert.Equal("E", sidebar.ProfileInitials);
        Assert.Contains(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commands_ChangeTheSessionOrNavigate()
    {
        using var sidebar = Create();

        await sidebar.SwitchProfileCommand.ExecuteAsync(null);
        await sidebar.SignOutCommand.ExecuteAsync(null);
        sidebar.OpenServersCommand.Execute(null);

        Assert.Equal(new[] { "clear-profile", "sign-out:active" }, _session.Calls);
        Assert.Equal(AppRoute.Servers, _navigation.Current);
    }
}
