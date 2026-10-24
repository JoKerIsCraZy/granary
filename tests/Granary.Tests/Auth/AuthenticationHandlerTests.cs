using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Profiles;
using Granary.Tests.Fakes;

namespace Granary.Tests.Auth;

public sealed class AuthenticationHandlerTests
{
    private readonly AuthPipeline _pipeline = new();

    [Fact]
    public async Task AccountRequest_CarriesBearerAndIdentityButNoProfileHeaders()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token");

        await new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, TestContext.Current.CancellationToken);

        var sent = Assert.Single(_pipeline.Server.Requests);
        Assert.Equal("Bearer access-1", sent.Authorization);
        Assert.Null(sent.Header("X-Profile-Id"));
        Assert.Null(sent.Header("X-Profile-Token"));
        Assert.Equal("device-1", sent.Header("X-Silo-Device-Id"));
        Assert.Equal("windows", sent.Header("X-Silo-Device-Platform"));
        Assert.Equal("desktop", sent.Header("X-Silo-Client-Family"));
    }

    [Fact]
    public async Task ProfileRequest_CarriesTheSelectedProfile()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token");

        await _pipeline.Http.GetAsync(server.BaseUri, "api/v2/home", SiloAuth.Profile, Core.Api.SiloJsonContext.Default.DictionaryStringString,
            TestContext.Current.CancellationToken);

        var sent = Assert.Single(_pipeline.Server.Requests);
        Assert.Equal("7", sent.Header("X-Profile-Id"));
        Assert.Equal("pin-token", sent.Header("X-Profile-Token"));
    }

    [Fact]
    public async Task AnonymousRequest_CarriesIdentityButNoCredentials()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7");

        await new Core.Api.Auth.AuthApi(_pipeline.Http).GetProvidersAsync(server.BaseUri, TestContext.Current.CancellationToken);

        var sent = Assert.Single(_pipeline.Server.Requests);
        Assert.Null(sent.Authorization);
        Assert.Null(sent.Header("X-Profile-Id"));
        Assert.Equal("device-1", sent.Header("X-Silo-Device-Id"));
    }

    [Fact]
    public async Task Credentials_StayWithTheServerThatIssuedThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync("https://silo.test/media");
        await _pipeline.SignInAsync(server);

        // Same host, outside the saved base path; and another host entirely.
        await Assert.ThrowsAsync<SiloApiException>(() => new AccountApi(_pipeline.Http).GetMeAsync(new Uri("https://silo.test/other"), ct));
        await Assert.ThrowsAsync<SiloApiException>(() => new AccountApi(_pipeline.Http).GetMeAsync(new Uri("https://evil.test"), ct));

        Assert.All(_pipeline.Server.Requests, r => Assert.Null(r.Authorization));
        Assert.Equal(0, _pipeline.Server.RefreshCalls);
    }

    [Fact]
    public async Task RequestWhoseUrlLeavesTheServerBase_GetsNoHeadersAtAll()
    {
        var server = await _pipeline.AddServerAsync("https://silo.test/media");
        await _pipeline.SignInAsync(server);
        using var invoker = new HttpMessageInvoker(new Core.Http.ClientIdentityHandler(AuthPipeline.Identity)
        {
            InnerHandler = new Core.Http.AuthenticationHandler(_pipeline.Registry, _pipeline.Credentials, _pipeline.Refresher, _pipeline.Clock,
                _pipeline.Logs.For<Core.Http.AuthenticationHandler>()) { InnerHandler = _pipeline.Server },
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cdn.test/poster.jpg");
        request.SetAuth(SiloAuth.Account);
        request.SetServer(server.BaseUri);

        using var _ = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        var sent = Assert.Single(_pipeline.Server.Requests);
        Assert.Null(sent.Authorization);
        Assert.Null(sent.Header("X-Silo-Device-Id"));
    }

    [Fact]
    public async Task ConcurrentUnauthorizedRequests_ShareOneRefreshAndAllSucceed()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.RefreshDelay = TimeSpan.FromMilliseconds(100);
        // The server revokes access-1 early; every request finds out with a 401.
        _pipeline.Server.Intercept = r => r.Authorization == "Bearer access-1" && r.Path != "/api/v2/auth/refresh"
            ? FakeSiloServer.Problem(401, ProblemCodes.TokenRefreshRequired)
            : null;
        var account = new AccountApi(_pipeline.Http);

        var users = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => account.GetMeAsync(server.BaseUri, ct), ct)));

        Assert.All(users, u => Assert.Equal("alex", u.Username));
        Assert.Equal(1, _pipeline.Server.RefreshCalls);
        var stored = await _pipeline.Credentials.GetAsync(server.Id, ct);
        Assert.Equal("access-2", stored!.AccessToken);
        Assert.Equal("refresh-2", stored.RefreshToken);
        Assert.Equal(_pipeline.Clock.Now.AddHours(8), stored.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task Replay_SendsTheBodyAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path.EndsWith("/verify-pin", StringComparison.Ordinal)
            ? r.Authorization == "Bearer access-1"
                ? FakeSiloServer.Problem(401, ProblemCodes.InvalidToken)
                : FakeSiloServer.Json(200, """{"valid":true,"profile_token":"t"}""")
            : null;

        var result = await new ProfilesApi(_pipeline.Http).VerifyPinAsync(server.BaseUri, "7", "1234", ct);

        Assert.True(result.Valid);
        var attempts = _pipeline.Server.RequestsTo("/api/v2/profiles/7/verify-pin").ToList();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, a => Assert.Equal("""{"pin":"1234"}""", a.Body));
        Assert.Equal(1, attempts.Count(a => a.Header("X-Silo-Device-Id") == "device-1" && a.Authorization == "Bearer access-2"));
    }

    [Fact]
    public async Task SecondUnauthorizedAfterRefresh_IsNotRetriedAgain()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/account/me" ? FakeSiloServer.Problem(401, ProblemCodes.InvalidToken) : null;

        var error = await Assert.ThrowsAsync<SiloApiException>(() =>
            new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, TestContext.Current.CancellationToken));

        Assert.Equal(401, error.Status);
        Assert.Equal(2, _pipeline.Server.RequestsTo("/api/v2/account/me").Count());
        Assert.Equal(1, _pipeline.Server.RefreshCalls);
    }

    [Fact]
    public async Task RejectedRefresh_SignsOutAndSurfacesTheUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path switch
        {
            "/api/v2/account/me" => FakeSiloServer.Problem(401, ProblemCodes.InvalidToken),
            "/api/v2/auth/refresh" => FakeSiloServer.Problem(401, ProblemCodes.SessionExpired),
            _ => null,
        };

        var error = await Assert.ThrowsAsync<SiloApiException>(() => new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct));

        Assert.Equal(401, error.Status);
        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
        Assert.NotNull(_pipeline.Registry.Find(server.Id));
    }

    [Fact]
    public async Task SessionExpired_SignsOutWithoutTryingARefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/account/me" ? FakeSiloServer.Problem(401, ProblemCodes.SessionExpired) : null;

        await Assert.ThrowsAsync<SiloApiException>(() => new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct));

        Assert.Equal(0, _pipeline.Server.RefreshCalls);
        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
    }

    [Fact]
    public async Task RefreshUnavailable_FailsWithThe503AndKeepsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path switch
        {
            "/api/v2/account/me" => FakeSiloServer.Problem(401, ProblemCodes.InvalidToken),
            "/api/v2/auth/refresh" => FakeSiloServer.Problem(503, ProblemCodes.DependencyUnavailable, retryAfterSeconds: 5),
            _ => null,
        };

        var error = await Assert.ThrowsAsync<SiloApiException>(() => new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct));

        Assert.Equal(503, error.Status);
        Assert.Equal(ProblemCodes.DependencyUnavailable, error.Code);
        Assert.Equal(TimeSpan.FromSeconds(5), error.RetryAfter);
        Assert.Equal("access-1", (await _pipeline.Credentials.GetAsync(server.Id, ct))!.AccessToken);
    }

    [Fact]
    public async Task RefreshValidationFailure_KeepsTheSessionAndSurfacesTheUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path switch
        {
            "/api/v2/account/me" => FakeSiloServer.Problem(401, ProblemCodes.InvalidToken),
            "/api/v2/auth/refresh" => FakeSiloServer.Problem(422, ProblemCodes.ValidationFailed),
            _ => null,
        };

        var error = await Assert.ThrowsAsync<SiloApiException>(() => new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct));

        Assert.Equal(401, error.Status);
        Assert.NotNull(await _pipeline.Credentials.GetAsync(server.Id, ct));
    }

    [Fact]
    public async Task TokenCloseToExpiry_IsRefreshedBeforeTheRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Clock.Now += TimeSpan.FromHours(8) - TimeSpan.FromSeconds(30);

        await new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct);

        Assert.Equal(new[] { "/api/v2/auth/refresh", "/api/v2/account/me" }, _pipeline.Server.Requests.Select(r => r.Path));
        Assert.Equal("Bearer access-2", _pipeline.Server.RequestsTo("/api/v2/account/me").Single().Authorization);
    }

    [Fact]
    public async Task EarlyRefreshThatFails_StillTriesTheCurrentToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Clock.Now += TimeSpan.FromHours(8) - TimeSpan.FromSeconds(30);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/auth/refresh" ? FakeSiloServer.Problem(503, ProblemCodes.DependencyUnavailable) : null;

        var me = await new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct);

        Assert.Equal("alex", me.Username);
        Assert.Equal("Bearer access-1", _pipeline.Server.RequestsTo("/api/v2/account/me").Single().Authorization);
    }

    [Fact]
    public async Task ProfileVerificationRequired_ClearsTheProfileButKeepsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "old-pin-token");
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/home" ? FakeSiloServer.Problem(403, ProblemCodes.ProfileVerificationRequired) : null;

        var error = await Assert.ThrowsAsync<SiloApiException>(() =>
            _pipeline.Http.GetAsync(server.BaseUri, "api/v2/home", SiloAuth.Profile, Core.Api.SiloJsonContext.Default.DictionaryStringString, ct));

        Assert.Equal(ProblemCodes.ProfileVerificationRequired, error.Code);
        var stored = await _pipeline.Credentials.GetAsync(server.Id, ct);
        Assert.NotNull(stored);
        Assert.Null(stored.ProfileId);
        Assert.Null(stored.ProfileToken);
    }

    [Fact]
    public async Task OtherForbidden_LeavesTheProfileAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7");
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/home" ? FakeSiloServer.Problem(403, ProblemCodes.PermissionDenied) : null;

        await Assert.ThrowsAsync<SiloApiException>(() =>
            _pipeline.Http.GetAsync(server.BaseUri, "api/v2/home", SiloAuth.Profile, Core.Api.SiloJsonContext.Default.DictionaryStringString, ct));

        Assert.Equal("7", (await _pipeline.Credentials.GetAsync(server.Id, ct))!.ProfileId);
    }

    [Fact]
    public async Task NoTokenEverReachesTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token-secret");
        _pipeline.Clock.Now += TimeSpan.FromHours(8);
        var account = new AccountApi(_pipeline.Http);
        await account.GetMeAsync(server.BaseUri, ct);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/auth/refresh" ? FakeSiloServer.Problem(401, ProblemCodes.InvalidToken) : null;
        _pipeline.Clock.Now += TimeSpan.FromHours(8);
        await Assert.ThrowsAsync<SiloApiException>(() => account.GetMeAsync(server.BaseUri, ct));

        Assert.NotEmpty(_pipeline.Logs.Lines);
        foreach (string secret in _pipeline.IssuedSecrets().Append("pin-token-secret"))
        {
            Assert.DoesNotContain(secret, _pipeline.Logs.All, StringComparison.Ordinal);
        }
    }
}
