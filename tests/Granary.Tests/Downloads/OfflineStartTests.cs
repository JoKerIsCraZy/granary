using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Granary started while its server cannot be reached: the remembered profile's downloads open instead of Home, with a note
/// and Retry, as Silo's Android app opens its downloads. A profile without downloads, a start without a remembered
/// profile and a server that answers anything of its own start as before.
/// </summary>
public sealed class OfflineStartTests : IAsyncDisposable
{
    private readonly DownloadHarness _h = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly FakeConfirmation _confirmation = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private OfflineStart Offline(DownloadStore? store = null) =>
        new(store ?? _h.Store, _h.Api, _h.Session, _h.Time, _h.Logs.For<OfflineStart>());

    private ShellViewModel Shell(OfflineStart? offline, IUiDispatcher? dispatcher = null) =>
        new(_h.Session, _navigation, dispatcher ?? new ImmediateDispatcher(), _h.Logs.For<ShellViewModel>(), offline);

    private DownloadsViewModel Page(OfflineStart offline) =>
        new(_h.NewManager(), _h.Disk, _h.Preferences, _navigation, _confirmation, new ImmediateDispatcher(), _h.Time,
            _h.Logs.For<DownloadsViewModel>(), offline);

    private async Task<DownloadsViewModel> ShownAsync(OfflineStart offline)
    {
        var page = Page(offline);
        page.Attach();
        await page.SpaceRead;
        return page;
    }

    private void Unreachable() => _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.NetworkError());

    private void Reachable() => _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability()));

    private static HttpResponseMessage CapabilityAnswer() =>
        new(HttpStatusCode.OK) { Content = new StringContent(DownloadPayloads.Capability(), Encoding.UTF8, "application/json") };

    private IReadOnlyList<SentRequest> Asked => _h.Silo.SentTo(HttpMethod.Get, CapabilityPath);

    // Profile 7 has a download, the server cannot be reached, and Granary has started: the Downloads page shows.
    private async Task<(OfflineStart Offline, DownloadsViewModel Page)> StartedWithoutTheServerAsync()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        var offline = Offline();
        await Shell(offline).StartAsync(cancellationToken: Token);
        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
        return (offline, await ShownAsync(offline));
    }

    [Fact]
    public async Task AStartWithoutTheServer_OpensDownloads_WithTheNote()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        // As at Granary's start: nothing has read the downloads yet.
        var offline = Offline(_h.UnreadStore());
        var shell = Shell(offline);

        await shell.StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
        Assert.Single(_navigation.Navigations);
        Assert.True(shell.IsSignedIn);
        Assert.True(offline.IsOffline);
        var page = await ShownAsync(offline);
        Assert.True(page.ShowsOfflineNote);
        Assert.Equal("Can't reach your Silo server. Downloads on this PC still play.", DownloadsViewModel.OfflineNote);
        // Asked as the profile whose downloads would show.
        Assert.Equal("7", Assert.Single(Asked).AsProfile);
    }

    [Fact]
    public async Task AStartWithTheServer_GoesHomeAsToday()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Reachable();
        var offline = Offline();
        var shell = Shell(offline);

        await shell.StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Home], _navigation.Resets);
        Assert.Single(_navigation.Navigations);
        Assert.True(shell.IsSignedIn);
        Assert.False(offline.IsOffline);
        Assert.False((await ShownAsync(offline)).ShowsOfflineNote);
        Assert.Single(Asked);
    }

    [Fact]
    public async Task AStartWithoutDownloads_AsksNothing_AndGoesHome()
    {
        Unreachable();
        var offline = Offline(_h.UnreadStore());

        await Shell(offline).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Home], _navigation.Resets);
        Assert.False(offline.IsOffline);
        Assert.Empty(_h.Silo.Sent);
    }

    // One profile never sees another's downloads, so they are no reason to open the page for it.
    [Fact]
    public async Task AStartWithOnlyAnotherProfilesDownloads_AsksNothing_AndGoesHome()
    {
        await _h.AddAsync("dl_8", DownloadState.Completed, scope: Scope8);
        Unreachable();
        var offline = Offline();

        await Shell(offline).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Home], _navigation.Resets);
        Assert.False(offline.IsOffline);
        Assert.Empty(_h.Silo.Sent);
    }

    [Fact]
    public async Task AStartWithoutARememberedProfile_ShowsThePickerAsToday()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        _h.Session.State = SessionState.ProfileRequired;
        _h.Session.ProfileId = null;
        var offline = Offline();
        var shell = Shell(offline);

        await shell.StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.ProfilePicker], _navigation.Resets);
        Assert.False(shell.IsSignedIn);
        Assert.False(offline.IsOffline);
        Assert.Empty(_h.Silo.Sent);
    }

    // Whatever the server says of its own, it is there: Home shows what it has to say, as before.
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task AServerThatAnswersAProblem_IsReachable(int status)
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Problem(status, "problem"));
        var offline = Offline();

        await Shell(offline).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Home], _navigation.Resets);
        Assert.False(offline.IsOffline);
    }

    // What a reverse proxy or a tunnel says when the server behind it is down, as Silo's Android app tells them
    // (PlaybackSessionLifecycle.kt, isGatewayOrTunnelFailureStatus).
    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(520)]
    [InlineData(521)]
    [InlineData(522)]
    [InlineData(523)]
    [InlineData(524)]
    [InlineData(527)]
    [InlineData(530)]
    public async Task AGatewayAnsweringInTheServersPlace_IsNotTheServer(int status)
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Problem(status, "problem"));
        var offline = Offline();

        await Shell(offline).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
        Assert.True(offline.IsOffline);
    }

    // A proxy's own error page, without a problem body.
    [Fact]
    public async Task AGatewaysOwnErrorPage_IsNotTheServer()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html"),
        }));

        await Shell(Offline()).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
    }

    // A refusal the server did not write: a network's sign-in page (511), a proxy's or a filter's own 403 or 404. The
    // server's own refusals are problems; Home could only show an error for these, and the downloads would be out of reach.
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(511)]
    public async Task AnErrorPageThatIsNotTheServers_IsNotTheServer(int status)
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("<html>Sign in to this network</html>", Encoding.UTF8, "text/html"),
        }));
        var offline = Offline();

        await Shell(offline).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
        Assert.True(offline.IsOffline);
    }

    // JSON, but no problem of the server's: it names no type.
    [Fact]
    public async Task AnErrorInJsonThatIsNoProblemOfTheServers_IsNotTheServer()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.Forbidden, "{ }"));

        await Shell(Offline()).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
    }

    // A network's sign-in page answers every address with a page of its own, as a hotel's does.
    [Fact]
    public async Task APageThatIsNotTheServers_IsNotTheServer()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Sign in to this network</html>", Encoding.UTF8, "text/html"),
        }));

        await Shell(Offline()).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
    }

    [Fact]
    public async Task TheCheck_GivesUpAfter5s()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        // A server that never answers, and does not let go by itself either.
        var requests = new List<CancellationToken>();
        _h.Silo.OnCancellable(HttpMethod.Get, CapabilityPath, cancellationToken =>
        {
            lock (requests)
            {
                requests.Add(cancellationToken);
            }
            return new TaskCompletionSource<HttpResponseMessage>().Task;
        });
        var offline = Offline();
        var starting = Shell(offline).StartAsync(cancellationToken: Token);
        await Until(() => Asked.Count == 1, "the server was asked");

        _h.Time.Advance(OfflineStart.Budget - TimeSpan.FromMilliseconds(1));
        Assert.False(starting.IsCompleted);
        Assert.Empty(_navigation.Navigations);
        _h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await starting.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal(TimeSpan.FromSeconds(5), OfflineStart.Budget);
        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
        Assert.True(offline.IsOffline);
        // The request is given up, not left to run on beside the page.
        lock (requests)
        {
            Assert.True(Assert.Single(requests).IsCancellationRequested);
        }
    }

    // The downloads folder can be a drive that sleeps or a share that does not answer: the start does not wait for it.
    [Fact]
    public async Task DownloadsThatCannotBeReadInTime_StartAsToday()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        var store = _h.UnreadStore();
        // Not disposed: the load's thread may still be waking from it.
        var released = new ManualResetEventSlim();
        store.Loading = () => released.Wait(TimeSpan.FromSeconds(30));
        try
        {
            var offline = Offline(store);
            var starting = Shell(offline).StartAsync(cancellationToken: Token);
            await _h.Time.WaitForTimerAsync(Token);

            _h.Time.Advance(OfflineStart.Budget);
            await starting.WaitAsync(TimeSpan.FromSeconds(5), Token);

            Assert.Equal([AppRoute.Home], _navigation.Resets);
            Assert.False(offline.IsOffline);
            Assert.Empty(_h.Silo.Sent);
        }
        finally
        {
            released.Set();
            await store.LoadAsync(Token);
        }
    }

    // The start screen waits at most 6 s: the read of the downloads and the question share the 5 s.
    [Fact]
    public async Task ASlowReadAndASilentServer_EndTogetherAfter5s()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.OnCancellable(HttpMethod.Get, CapabilityPath, _ => new TaskCompletionSource<HttpResponseMessage>().Task);
        var store = _h.UnreadStore();
        // Not disposed: the load's thread may still be waking from it.
        var released = new ManualResetEventSlim();
        store.Loading = () => released.Wait(TimeSpan.FromSeconds(30));
        try
        {
            var offline = Offline(store);
            var starting = Shell(offline).StartAsync(cancellationToken: Token);
            await _h.Time.WaitForTimerAsync(Token);

            _h.Time.Advance(TimeSpan.FromSeconds(3));
            released.Set();
            await Until(() => Asked.Count == 1, "the server was asked");
            _h.Time.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(1));
            Assert.False(starting.IsCompleted);
            _h.Time.Advance(TimeSpan.FromMilliseconds(1));
            await starting.WaitAsync(TimeSpan.FromSeconds(5), Token);

            Assert.Equal(OfflineStart.Budget, _h.Time.Elapsed);
            Assert.Equal([AppRoute.Downloads], _navigation.Resets);
            Assert.True(offline.IsOffline);
        }
        finally
        {
            released.Set();
            await store.LoadAsync(Token);
        }
    }

    [Fact]
    public async Task DownloadsThatCannotBeRead_StartAsToday()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        var store = _h.UnreadStore();
        store.Loading = () => throw new IOException("The device is not ready.");
        var offline = Offline(store);

        await Shell(offline).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Home], _navigation.Resets);
        Assert.False(offline.IsOffline);
        Assert.Empty(_h.Silo.Sent);
        // A file error's message can name the full path, with the Windows user name: only its kind is logged.
        Assert.DoesNotContain("not ready", _h.Logs.All, StringComparison.Ordinal);
    }

    // The real session says where it stands while it is restored, before the start has asked the server: that must not
    // open Home under the start screen first.
    [Fact]
    public async Task ASessionThatSaysItIsReadyWhileItIsRestored_DoesNotOpenHomeFirst()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        _h.Session.Restoring = _h.Session.RaiseChanged;

        await Shell(Offline()).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Downloads], _navigation.Navigations.Select(n => n.Route));
    }

    [Fact]
    public async Task ASessionThatSaysItIsReadyWhileItIsRestored_StillGoesHomeOnce_WithTheServer()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Reachable();
        _h.Session.Restoring = _h.Session.RaiseChanged;

        await Shell(Offline()).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Home], _navigation.Navigations.Select(n => n.Route));
    }

    // As in the app: the session says so from another thread, and the route it asks for is posted to the UI thread, where
    // it runs while the server is still asked.
    [Fact]
    public async Task ARouteTheSessionPostedWhileTheServerIsAsked_Waits()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => answer.Task);
        var posted = new PostedWork();
        _h.Session.Restoring = _h.Session.RaiseChanged;
        var starting = Shell(Offline(), posted).StartAsync(cancellationToken: Token);
        await Until(() => Asked.Count == 1, "the server was asked");

        Assert.Equal(1, posted.Run());
        Assert.Empty(_navigation.Navigations);
        answer.SetException(new HttpRequestException(HttpRequestError.ConnectionError));
        await starting;

        Assert.Equal([AppRoute.Downloads], _navigation.Navigations.Select(n => n.Route));
    }

    // The sign-in can end while the server is asked, as when it turns out to have expired: the start goes where the
    // session then stands.
    [Fact]
    public async Task ASignOutWhileTheServerIsAsked_OpensSignIn()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () =>
        {
            _h.Session.State = SessionState.SignedOut;
            _h.Session.ProfileId = null;
            return ScriptedSilo.NetworkError()();
        });
        var offline = Offline();
        var shell = Shell(offline);

        await shell.StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.SignIn], _navigation.Resets);
        Assert.False(shell.IsSignedIn);
        Assert.False(offline.IsOffline);
    }

    [Fact]
    public async Task AStartCalledOffWhileTheServerIsAsked_StillRoutes()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.OnCancellable(HttpMethod.Get, CapabilityPath, _ => new TaskCompletionSource<HttpResponseMessage>().Task);
        using var calledOff = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var offline = Offline();
        var starting = Shell(offline).StartAsync(cancellationToken: calledOff.Token);
        await Until(() => Asked.Count == 1, "the server was asked");

        await calledOff.CancelAsync();
        await starting.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal([AppRoute.Home], _navigation.Resets);
        Assert.False(offline.IsOffline);
    }

    // Whatever goes wrong while Granary finds out, it starts: a start that failed here would end the app.
    [Fact]
    public async Task AListenerThatThrows_DoesNotStopTheStart()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        var offline = Offline();
        offline.PropertyChanged += (_, _) => throw new InvalidOperationException("A listener failed.");

        await Shell(offline).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Home], _navigation.Resets);
    }

    // The question is bound to the profile it is asked for: when the server acts as another one, it is not sent.
    [Fact]
    public async Task AQuestionThatCannotGoOutAsItsProfile_StartsAsToday()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        _h.Silo.ActingProfile = () => "8";
        var offline = Offline();

        await Shell(offline).StartAsync(cancellationToken: Token);

        Assert.Equal([AppRoute.Home], _navigation.Resets);
        Assert.False(offline.IsOffline);
        Assert.Empty(_h.Silo.Sent);
    }

    [Fact]
    public async Task ADeveloperStartRoute_IsNotChecked()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        var offline = Offline();
        var shell = Shell(offline);

        await shell.StartAsync(AppRoute.Player, "movie.mkv", Token);

        Assert.Equal((AppRoute.Player, (object?)"movie.mkv"), Assert.Single(_navigation.Navigations));
        Assert.True(shell.IsSignedIn);
        Assert.False(offline.IsOffline);
        Assert.Empty(_h.Silo.Sent);
    }

    // As before the offline start: the restored session opens Home, and the developer's route opens over it.
    [Fact]
    public async Task ADeveloperStartRoute_StillOpensOverHome_WhenTheRestoredSessionSaidItIsReady()
    {
        _h.Session.Restoring = _h.Session.RaiseChanged;

        await Shell(Offline()).StartAsync(AppRoute.Player, "movie.mkv", Token);

        Assert.Equal([AppRoute.Home, AppRoute.Player], _navigation.Navigations.Select(n => n.Route));
    }

    [Fact]
    public async Task Retry_GoesHome_OnceTheServerAnswers()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();
        Reachable();

        await page.RetryConnectionCommand.ExecuteAsync(null);

        // As the sidebar's Home: a reset is a new start, which would close a download playing in the minimized player.
        Assert.Equal([AppRoute.Home], _navigation.Sections);
        Assert.Equal([AppRoute.Downloads], _navigation.Resets);
        Assert.Equal(AppRoute.Home, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
        Assert.False(offline.IsOffline);
        Assert.False(page.ShowsOfflineNote);
        Assert.False(page.IsRetrying);
    }

    [Fact]
    public async Task Retry_KeepsTheNote_WhileTheServerStillCannotBeReached()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();

        await page.RetryConnectionCommand.ExecuteAsync(null);

        Assert.Single(_navigation.Navigations);
        Assert.True(offline.IsOffline);
        Assert.True(page.ShowsOfflineNote);
        Assert.False(page.IsRetrying);
        Assert.Equal(2, Asked.Count);
    }

    // The button stays as it is while the server is asked, so a keyboard's or a remote's focus stays on it; a second press
    // asks nothing more.
    [Fact]
    public async Task ARetryUnderWay_ShowsItself_AndIsNotStartedTwice()
    {
        var (_, page) = await StartedWithoutTheServerAsync();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => answer.Task);

        var retrying = page.RetryConnectionCommand.ExecuteAsync(null);
        await Until(() => Asked.Count == 2, "the server was asked again");
        Assert.True(page.IsRetrying);
        Assert.True(page.RetryConnectionCommand.CanExecute(null));
        await page.RetryConnectionCommand.ExecuteAsync(null);
        Assert.Equal(2, Asked.Count);

        answer.SetResult(CapabilityAnswer());
        await retrying;

        Assert.False(page.IsRetrying);
        Assert.Equal([AppRoute.Home], _navigation.Sections);
    }

    [Fact]
    public async Task ARetry_GivesUpAfter5sToo()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();
        _h.Silo.OnCancellable(HttpMethod.Get, CapabilityPath, _ => new TaskCompletionSource<HttpResponseMessage>().Task);

        var retrying = page.RetryConnectionCommand.ExecuteAsync(null);
        await Until(() => Asked.Count == 2, "the server was asked again");
        _h.Time.Advance(OfflineStart.Budget);
        await retrying.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Single(_navigation.Navigations);
        Assert.True(offline.IsOffline);
        Assert.True(page.ShowsOfflineNote);
        Assert.False(page.IsRetrying);
    }

    // The viewer went on meanwhile, to a download's player for one: an answer that comes then takes them nowhere.
    [Fact]
    public async Task ARetryAnsweredAfterThePageWasLeft_MovesNobody()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => answer.Task);
        var retrying = page.RetryConnectionCommand.ExecuteAsync(null);
        await Until(() => Asked.Count == 2, "the server was asked again");

        page.Detach();
        answer.SetResult(CapabilityAnswer());
        await retrying;

        Assert.Single(_navigation.Navigations);
        Assert.False(offline.IsOffline);
        Assert.False(page.IsRetrying);
    }

    // Left and shown again before the answer: the viewer came back from somewhere else, and is not sent off by a press
    // from before.
    [Fact]
    public async Task ARetryAnsweredAfterThePageWasLeftAndShownAgain_MovesNobody()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Get, CapabilityPath, () => answer.Task);
        var retrying = page.RetryConnectionCommand.ExecuteAsync(null);
        await Until(() => Asked.Count == 2, "the server was asked again");

        page.Detach();
        page.Attach();
        await page.SpaceRead;
        answer.SetResult(CapabilityAnswer());
        await retrying;

        Assert.Single(_navigation.Navigations);
        Assert.False(offline.IsOffline);
        Assert.False(page.ShowsOfflineNote);
        Assert.False(page.IsRetrying);
    }

    // The account's details read again, as the session does in the background at every start: the same server and profile.
    [Fact]
    public async Task ASessionUpdateAfterAStartWithoutTheServer_MovesNobody()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();

        _h.Session.RaiseChanged();

        Assert.Single(_navigation.Navigations);
        Assert.True(offline.IsOffline);
        Assert.True(page.ShowsOfflineNote);
    }

    // The note is about the server and profile Granary started with.
    [Fact]
    public async Task ASignOut_TakesTheNoteAway()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();

        _h.SignOut();

        Assert.Equal([AppRoute.Downloads, AppRoute.SignIn], _navigation.Resets);
        Assert.False(offline.IsOffline);
        Assert.False(page.ShowsOfflineNote);
    }

    [Fact]
    public async Task AnotherProfile_GetsNoNote()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();

        _h.SwitchProfile("8");

        Assert.Equal([AppRoute.Downloads, AppRoute.Home], _navigation.Resets);
        Assert.False(offline.IsOffline);
        Assert.False(page.ShowsOfflineNote);
    }

    // The session moved on without saying so yet: the note was the other profile's, and nothing is asked for this one.
    [Fact]
    public async Task ARetryForAProfileThatIsNoLongerTheActiveOne_AsksNothing_AndTheNoteGoes()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();
        _h.Session.ProfileId = "8";

        await page.RetryConnectionCommand.ExecuteAsync(null);

        Assert.Single(Asked);
        Assert.Single(_navigation.Navigations);
        Assert.False(offline.IsOffline);
        Assert.False(page.ShowsOfflineNote);
        Assert.False(page.IsRetrying);
    }

    [Fact]
    public async Task ARetryThatCannotGoOutAsItsProfile_KeepsTheNote()
    {
        var (offline, page) = await StartedWithoutTheServerAsync();
        _h.Silo.ActingProfile = () => "8";

        await page.RetryConnectionCommand.ExecuteAsync(null);

        Assert.Single(Asked);
        Assert.Single(_navigation.Navigations);
        Assert.True(offline.IsOffline);
        Assert.False(page.IsRetrying);
    }

    [Fact]
    public async Task Retry_WithNothingToRetry_AsksNothing()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        var offline = Offline();

        Assert.False(await offline.RetryAsync(Token));

        Assert.Empty(_h.Silo.Sent);
    }

    [Fact]
    public async Task APageWithoutAnOfflineStart_ShowsNoNote_AndItsRetryDoesNothing()
    {
        var page = new DownloadsViewModel(_h.NewManager(), _h.Disk, _h.Preferences, _navigation, _confirmation, new ImmediateDispatcher(), _h.Time,
            _h.Logs.For<DownloadsViewModel>());
        page.Attach();
        await page.SpaceRead;

        await page.RetryConnectionCommand.ExecuteAsync(null);

        Assert.False(page.ShowsOfflineNote);
        Assert.Empty(_navigation.Navigations);
    }

    // The offline start lives as long as the app. A page it still told of its changes would stay in memory for good, with
    // everything its bindings hold.
    [Fact]
    public async Task APageThatWasLeft_IsNotKeptByTheOfflineStart()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed);
        Unreachable();
        var offline = Offline();
        await Shell(offline).StartAsync(cancellationToken: Token);
        var (page, spaceRead) = ShowPage(offline);
        await spaceRead;

        Leave(page);

        Assert.True(await Collected.IsGoneAsync(page));
        Assert.True(offline.IsOffline);
        GC.KeepAlive(offline);
    }

    // Made in a method of its own, so no local of the test keeps the page.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference<ShownPage> Page, Task SpaceRead) ShowPage(OfflineStart offline)
    {
        var model = Page(offline);
        var page = new ShownPage(model);
        model.Attach();
        Assert.True(model.ShowsOfflineNote);
        return (new WeakReference<ShownPage>(page), model.SpaceRead);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Leave(WeakReference<ShownPage> page)
    {
        if (page.TryGetTarget(out var shown))
        {
            shown.ViewModel.Detach();
        }
    }

    // A UI thread the test runs by hand: posted work waits until the test runs it.
    private sealed class PostedWork : IUiDispatcher
    {
        private readonly Queue<Action> _work = new();

        public bool HasThreadAccess => false;

        public void Post(Action action)
        {
            lock (_work)
            {
                _work.Enqueue(action);
            }
        }

        /// <summary>Runs what was posted so far, and answers how much that was.</summary>
        public int Run()
        {
            Action[] work;
            lock (_work)
            {
                work = [.. _work];
                _work.Clear();
            }
            foreach (var action in work)
            {
                action();
            }
            return work.Length;
        }
    }

    // Stands in for DownloadsPage: it holds its view model and listens to it, as the page's bindings do.
    private sealed class ShownPage
    {
        public ShownPage(DownloadsViewModel viewModel)
        {
            ViewModel = viewModel;
            viewModel.PropertyChanged += OnChanged;
        }

        public DownloadsViewModel ViewModel { get; }

        public int Changes { get; private set; }

        private void OnChanged(object? sender, PropertyChangedEventArgs e) => Changes++;
    }
}
