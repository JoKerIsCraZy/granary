using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.Api.Settings;
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

    // The profile's hidden libraries and order (ui.disabled_library_ids, ui.library_order): none by default.
    private Func<HttpResponseMessage> _settings = () => FakeSiloServer.Json(200,
        """{"items":[{"key":"ui.disabled_library_ids","value":null},{"key":"ui.library_order","value":null}],"revision":1}""");

    private readonly ManualTimeProvider _clock = new();

    // The next library read waits for this, answering as the libraries did when it was asked.
    private TaskCompletionSource? _holdLibraries;

    private SidebarViewModel Create()
    {
        var handler = new FakeHttpMessageHandler(async (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            _requests.Add(path);
            if (path.EndsWith("/user/libraries", StringComparison.Ordinal))
            {
                var respond = _libraries;
                if (_holdLibraries is { } hold)
                {
                    _holdLibraries = null;
                    await hold.Task;
                }
                return respond();
            }
            return path.EndsWith("/settings/values/effective", StringComparison.Ordinal) ? _settings() : _profiles();
        });
        var http = new SiloHttp(new HttpClient(handler));
        var libraries = new VisibleLibraries(new CatalogApi(http), new SettingsApi(http), _logs.For<VisibleLibraries>());
        return new SidebarViewModel(_session, new ProfilesApi(http), libraries, _navigation, new ImmediateDispatcher(), _clock,
            _logs.For<SidebarViewModel>());
    }

    private static Func<HttpResponseMessage> Hiding(string ids) => () => FakeSiloServer.Json(200,
        $$"""{"items":[{"key":"ui.disabled_library_ids","value":{{ids}}},{"key":"ui.library_order","value":null}],"revision":2}""");

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
    public async Task TheProfilesHiddenLibraries_AreLeftOut_AndItsOrderKept()
    {
        Ready();
        _libraries = () => FakeSiloServer.Json(200,
            """{"items":[{"id":"1","name":"Filme","type":"movies","sort_order":1},{"id":"2","name":"Serien","type":"series","sort_order":2},{"id":"3","name":"Filme-4k","type":"movies","sort_order":3}]}""");
        _settings = () => FakeSiloServer.Json(200,
            """{"items":[{"key":"ui.disabled_library_ids","value":[2]},{"key":"ui.library_order","value":"[3,1]"}],"revision":1}""");
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Filme-4k", "Filme"], sidebar.Libraries.Select(l => l.Name));
    }

    [Fact]
    public async Task PreferencesThatFail_HideNoLibrary()
    {
        Ready();
        _settings = () => FakeSiloServer.Problem(503, ProblemCodes.DependencyUnavailable);
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Filme", "Serien"], sidebar.Libraries.Select(l => l.Name));
    }

    [Fact]
    public async Task HiddenLibrariesThatCouldNotBeRead_AreAskedForAgain_AndThenLeftOut()
    {
        Ready();
        _settings = () => FakeSiloServer.Problem(503, ProblemCodes.DependencyUnavailable);
        using var sidebar = Create();
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Filme", "Serien"], sidebar.Libraries.Select(l => l.Name));

        _settings = Hiding("[2]");
        _clock.Advance(SidebarViewModel.RetryAfter);

        await Auth.AuthPipeline.Eventually(() => sidebar.Libraries.Count == 1);
        Assert.Equal("Filme", sidebar.Libraries[0].Name);
        Assert.Equal("Elena Moreau", sidebar.ProfileName);
    }

    [Fact]
    public async Task ALibraryReadThatTimesOut_LeavesNothingStuck_AndIsTriedAgain()
    {
        Ready();
        _libraries = () => throw new TaskCanceledException("The request timed out.");
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(sidebar.Libraries);
        Assert.False(sidebar.AreLibrariesLoaded);

        _libraries = () => FakeSiloServer.Json(200, """{"items":[{"id":"1","name":"Filme","type":"movies","sort_order":1}]}""");
        _clock.Advance(SidebarViewModel.RetryAfter);

        await Auth.AuthPipeline.Eventually(() => sidebar.Libraries.Count == 1);
        Assert.True(sidebar.AreLibrariesLoaded);
    }

    [Fact]
    public async Task HiddenLibrariesThatTimeOut_ShowEveryLibraryForNow_AndAreTriedAgain()
    {
        Ready();
        _settings = () => throw new TaskCanceledException("The request timed out.");
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, sidebar.Libraries.Count);

        _settings = Hiding("[1]");
        _clock.Advance(SidebarViewModel.RetryAfter);

        await Auth.AuthPipeline.Eventually(() => sidebar.Libraries.Count == 1);
        Assert.Equal("Serien", sidebar.Libraries[0].Name);
    }

    [Fact]
    public async Task OnceStale_TheLibrariesAreReadAgain_InPlace()
    {
        Ready();
        using var sidebar = Create();
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        int asked = _requests.Count;
        _settings = Hiding("[2]");
        var cleared = false;
        sidebar.Libraries.CollectionChanged += (_, e) => cleared |= sidebar.Libraries.Count == 0;

        _clock.Advance(SidebarViewModel.StaleAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(asked, _requests.Count);
        _clock.Advance(TimeSpan.FromSeconds(1));

        await Auth.AuthPipeline.Eventually(() => sidebar.Libraries.Count == 1);
        Assert.Equal("Elena Moreau", sidebar.ProfileName);
        // Only the list that changed was redrawn; the profile never went away.
        Assert.True(cleared);
        Assert.NotNull(sidebar.Profile);
    }

    [Fact]
    public async Task AnOlderRefreshThatFails_LeavesTheNewerProfileLoaded()
    {
        Ready();
        _libraries = () => FakeSiloServer.Problem(503, ProblemCodes.DependencyUnavailable);
        var hold = new TaskCompletionSource();
        _holdLibraries = hold;
        using var sidebar = Create();
        var older = sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        _libraries = () => FakeSiloServer.Json(200, """{"items":[{"id":"1","name":"Filme","type":"movies","sort_order":1}]}""");
        _session.ProfileId = "8";
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        hold.SetResult();
        await older;
        int asked = _requests.Count;

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(asked, _requests.Count);
        Assert.Equal("Filme", Assert.Single(sidebar.Libraries).Name);
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

        // The profile, its libraries and their preferences, once.
        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public async Task ProfileChange_ReloadsThroughTheSessionEvent()
    {
        Ready();
        using var sidebar = Create();
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await Auth.AuthPipeline.Eventually(() => _requests.Count == 6);

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
        // The servers are in Settings → Servers now.
        Assert.Equal((AppRoute.Settings, (object?)"servers"), _navigation.Navigations[^1]);
    }

    [Fact]
    public async Task AdminAccount_OffersAdmin_OthersDoNot()
    {
        Ready();
        using var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(sidebar.IsAdmin);

        // Another account on the same server and profile: the role is read again, not kept with the loaded sidebar.
        _session.Account = new Account("2", "kim", "", "user", [], true, false, null);
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(sidebar.IsAdmin);

        _session.Account = new Account("1", "elena", "", "admin", [], true, false, null);
        _session.State = SessionState.SignedOut;
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(sidebar.IsAdmin);
    }
}
