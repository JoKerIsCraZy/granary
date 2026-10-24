using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Tests.Fakes;

namespace Granary.Tests.Auth;

/// <summary>
/// Work started for one login (a refresh, a retry, a late error answer) must never change a later login
/// to the same server: the user may have signed out and in again, possibly as someone else.
/// </summary>
public sealed class SessionPinningTests
{
    private readonly AuthPipeline _pipeline = new();

    private Task<Dictionary<string, string>> GetHomeAsync(Uri server) =>
        _pipeline.Http.GetAsync(server, "api/v2/home", SiloAuth.Profile, SiloJsonContext.Default.DictionaryStringString,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task RefreshFinishingAfterANewLogin_DoesNotOverwriteIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7");
        _pipeline.Server.RefreshDelay = TimeSpan.FromMilliseconds(300);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/home" && r.Authorization == "Bearer access-1"
            ? FakeSiloServer.Problem(401, ProblemCodes.TokenRefreshRequired)
            : null;

        var request = Task.Run(() => GetHomeAsync(server.BaseUri), ct);
        await AuthPipeline.Eventually(() => _pipeline.Server.RefreshCalls == 1);
        var bob = await _pipeline.SignInAsSomeoneElseAsync(server);

        var error = await Assert.ThrowsAsync<SiloApiException>(() => request);
        Assert.Equal(401, error.Status);
        Assert.Equal(bob, await _pipeline.Credentials.GetAsync(server.Id, ct));
        // The old login's request was never replayed with the new login's credentials.
        Assert.DoesNotContain(_pipeline.Server.Requests, r => r.Authorization == "Bearer bob-access");
    }

    [Fact]
    public async Task LateSessionExpiredForAnOldLogin_KeepsTheNewLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7");
        using var answered = new ManualResetEventSlim();
        _pipeline.Server.Intercept = r =>
        {
            if (r.Path != "/api/v2/home")
            {
                return null;
            }
            answered.Wait(TimeSpan.FromSeconds(5));
            return FakeSiloServer.Problem(401, ProblemCodes.SessionExpired);
        };

        var request = Task.Run(() => GetHomeAsync(server.BaseUri), ct);
        await AuthPipeline.Eventually(() => _pipeline.Server.RequestsTo("/api/v2/home").Any());
        var bob = await _pipeline.SignInAsSomeoneElseAsync(server);
        answered.Set();

        await Assert.ThrowsAsync<SiloApiException>(() => request);
        Assert.Equal(bob, await _pipeline.Credentials.GetAsync(server.Id, ct));
    }

    [Fact]
    public async Task Replay_ActsAsTheProfileTheRequestWasMadeFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-7");
        _pipeline.Server.RefreshDelay = TimeSpan.FromMilliseconds(300);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/home" && r.Authorization == "Bearer access-1"
            ? FakeSiloServer.Problem(401, ProblemCodes.TokenRefreshRequired)
            : null;

        var request = Task.Run(() => GetHomeAsync(server.BaseUri), ct);
        await AuthPipeline.Eventually(() => _pipeline.Server.RefreshCalls == 1);
        await _pipeline.Credentials.UpdateAsync(server.Id, c => c with { ProfileId = "9", ProfileToken = "pin-9" }, ct);
        await request;

        var attempts = _pipeline.Server.RequestsTo("/api/v2/home").ToList();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, a => Assert.Equal("7", a.Header("X-Profile-Id")));
        Assert.All(attempts, a => Assert.Equal("pin-7", a.Header("X-Profile-Token")));
        Assert.Equal("Bearer access-2", attempts[1].Authorization);
        Assert.Equal("9", (await _pipeline.Credentials.GetAsync(server.Id, ct))!.ProfileId);
    }

    [Fact]
    public async Task ProfileRejectedForAnOldPinToken_KeepsTheReverifiedProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "old-pin-token");
        using var answered = new ManualResetEventSlim();
        _pipeline.Server.Intercept = r =>
        {
            if (r.Path != "/api/v2/home")
            {
                return null;
            }
            answered.Wait(TimeSpan.FromSeconds(5));
            return FakeSiloServer.Problem(403, ProblemCodes.ProfileVerificationRequired);
        };

        var request = Task.Run(() => GetHomeAsync(server.BaseUri), ct);
        await AuthPipeline.Eventually(() => _pipeline.Server.RequestsTo("/api/v2/home").Any());
        await _pipeline.Credentials.UpdateAsync(server.Id, c => c with { ProfileToken = "new-pin-token" }, ct);
        answered.Set();

        await Assert.ThrowsAsync<SiloApiException>(() => request);
        var stored = await _pipeline.Credentials.GetAsync(server.Id, ct);
        Assert.Equal("7", stored!.ProfileId);
        Assert.Equal("new-pin-token", stored.ProfileToken);
    }

    [Fact]
    public async Task ReplayAnsweredWithSessionExpired_SignsOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/account/me"
            ? FakeSiloServer.Problem(401, r.Authorization == "Bearer access-1" ? ProblemCodes.InvalidToken : ProblemCodes.SessionExpired)
            : null;

        await Assert.ThrowsAsync<SiloApiException>(() => new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct));

        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
    }

    [Fact]
    public async Task FailedRefresh_IsSharedByEveryoneWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.RefreshDelay = TimeSpan.FromMilliseconds(100);
        _pipeline.Server.Intercept = r => r.Path switch
        {
            "/api/v2/account/me" => FakeSiloServer.Problem(401, ProblemCodes.InvalidToken),
            "/api/v2/auth/refresh" => FakeSiloServer.Problem(503, ProblemCodes.DependencyUnavailable, retryAfterSeconds: 5),
            _ => null,
        };
        var account = new AccountApi(_pipeline.Http);

        var calls = Enumerable.Range(0, 10).Select(_ => Task.Run(() => account.GetMeAsync(server.BaseUri, ct), ct)).ToList();

        foreach (var call in calls)
        {
            Assert.Equal(503, (await Assert.ThrowsAsync<SiloApiException>(() => call)).Status);
        }
        Assert.Single(_pipeline.Server.RequestsTo("/api/v2/auth/refresh"));
    }

    [Fact]
    public async Task CancelledCaller_StopsWaitingButTheRefreshIsStillStored()
    {
        var server = await _pipeline.AddServerAsync();
        var alice = await _pipeline.SignInAsync(server);
        _pipeline.Server.RefreshDelay = TimeSpan.FromMilliseconds(500);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _pipeline.Refresher.RefreshAsync(server, alice, cancel.Token));

        await AuthPipeline.Eventually(async () => (await _pipeline.Credentials.GetAsync(server.Id))?.AccessToken == "access-2");
    }

    [Fact]
    public async Task UnusableTokens_AreRefusedAtSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/auth/login"
            ? FakeSiloServer.Json(200, """{"access_token":"a\r\nX-Injected: 1","refresh_token":"r","expires_in":28800,"user":{"id":"1","username":"alex","email":"","role":"user","permissions":[],"download_allowed":true,"password_change_required":false}}""")
            : null;
        using var session = _pipeline.CreateSessionManager();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => session.SignInWithPasswordAsync(server, "alex", "pw", ct));

        Assert.Equal(HttpRequestError.InvalidResponse, error.HttpRequestError);
        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(long.MaxValue)]
    public async Task UnusableLifetime_IsRefusedAtSignIn(long expiresIn)
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/auth/login"
            ? FakeSiloServer.Json(200, $$$"""{"access_token":"a","refresh_token":"r","expires_in":{{{expiresIn}}},"user":{"id":"1","username":"alex","email":"","role":"user","permissions":[],"download_allowed":true,"password_change_required":false}}""")
            : null;
        using var session = _pipeline.CreateSessionManager();

        await Assert.ThrowsAsync<HttpRequestException>(() => session.SignInWithPasswordAsync(server, "alex", "pw", ct));

        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
    }

    [Fact]
    public async Task ProfileValuesThatBreakHeaders_AreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);

        await Assert.ThrowsAsync<HttpRequestException>(() => session.SelectProfileAsync("7", "token\r\nX-Injected: 1", ct));

        Assert.Null((await _pipeline.Credentials.GetAsync(server.Id, ct))!.ProfileId);
    }

    [Fact]
    public async Task SigningInAgain_EndsTheReplacedLoginOnTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);

        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);

        Assert.Equal("Bearer access-1", _pipeline.Server.RequestsTo("/api/v2/auth/logout").Single().Authorization);
        Assert.NotNull(await _pipeline.Credentials.GetAsync(server.Id, ct));
    }

    [Fact]
    public async Task SignOut_ForgetsTheTokensEvenWhenTheCallerGivesUp()
    {
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SignOutAsync(server.Id, cancelled.Token));

        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonSiloAnswer_IsReportedAsAnInvalidResponse()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/account/me"
            ? FakeSiloServer.Json(200, "<html>Hotel Wi-Fi login</html>", "text/html")
            : null;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, TestContext.Current.CancellationToken));

        Assert.Equal(HttpRequestError.InvalidResponse, error.HttpRequestError);
    }
}
