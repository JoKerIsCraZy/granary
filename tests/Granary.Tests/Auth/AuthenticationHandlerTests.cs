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
    public async Task AWriteForAProfile_IsSentAsThatProfile()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token");

        await new Core.Api.Playback.TrackPreferencesApi(_pipeline.Http).DeleteAudioAsync(server.BaseUri, "movie-1", "7",
            TestContext.Current.CancellationToken);

        var sent = Assert.Single(_pipeline.Server.Requests);
        Assert.Equal(("7", "pin-token"), (sent.Header("X-Profile-Id"), sent.Header("X-Profile-Token")));
    }

    [Fact]
    public async Task AWriteForAProfile_IsNotSentOnceTheServerActsAsAnother()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "9");

        // Made by profile 7 and sent after the switch to 9: the check before the queue sends it and this send are apart.
        await Assert.ThrowsAsync<ProfileChangedException>(() => new Core.Api.Playback.TrackPreferencesApi(_pipeline.Http)
            .DeleteAudioAsync(server.BaseUri, "movie-1", "7", TestContext.Current.CancellationToken));

        Assert.Empty(_pipeline.Server.Requests);
    }

    [Fact]
    public async Task AWriteForAProfile_IsNotSentWithoutOne()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);

        await Assert.ThrowsAsync<ProfileChangedException>(() => new Core.Api.Playback.TrackPreferencesApi(_pipeline.Http)
            .DeleteAudioAsync(server.BaseUri, "movie-1", "7", TestContext.Current.CancellationToken));

        Assert.Empty(_pipeline.Server.Requests);
    }

    [Fact]
    public async Task ARequestForAnAccountsProfile_IsSentAsThatAccountAndProfile()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7");
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/progress" ? FakeSiloServer.Json(200, """{ "items": [], "page": { "has_more": false } }""") : null;

        await new Core.Api.Downloads.ProgressSyncApi(_pipeline.Http).ListAsync(server.BaseUri, "1", "7", null, TestContext.Current.CancellationToken);

        Assert.Equal("7", Assert.Single(_pipeline.Server.Requests).Header("X-Profile-Id"));
    }

    [Fact]
    public async Task ARequestForAnAccountsProfile_IsNotSentOnceAnotherAccountIsSignedIn_AlsoWithTheSameProfileId()
    {
        var server = await _pipeline.AddServerAsync();
        // Account 2 signed in meanwhile; its profile carries id 9, the one the request was made for under account 1.
        await _pipeline.SignInAsSomeoneElseAsync(server);

        await Assert.ThrowsAsync<ProfileChangedException>(() => new Core.Api.Downloads.ProgressSyncApi(_pipeline.Http)
            .ListAsync(server.BaseUri, "1", "9", null, TestContext.Current.CancellationToken));

        Assert.Empty(_pipeline.Server.Requests);
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
    public async Task ALentProfile_GoesInsteadOfTheSessions_WithItsOwnProof()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token");
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/profiles/9" ? FakeSiloServer.Json(204, "") : null;

        await new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", "owner-token"),
            TestContext.Current.CancellationToken);

        var sent = Assert.Single(_pipeline.Server.Requests);
        Assert.Equal("Bearer access-1", sent.Authorization);
        Assert.Equal(("1", "owner-token"), (sent.Header("X-Profile-Id"), sent.Header("X-Profile-Token")));
    }

    [Fact]
    public async Task ALentProfileWithoutAPin_GoesWithoutAProof_AlsoWhileNoProfileActs()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/profiles/9" ? FakeSiloServer.Json(204, "") : null;

        await new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", null), TestContext.Current.CancellationToken);

        var sent = Assert.Single(_pipeline.Server.Requests);
        Assert.Equal(("1", (string?)null), (sent.Header("X-Profile-Id"), sent.Header("X-Profile-Token")));
    }

    [Fact]
    public async Task ALentProfileTheServerWantsVerified_LeavesTheSessionsProfileAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token");
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/profiles/9" ? FakeSiloServer.Problem(403, ProblemCodes.ProfileVerificationRequired) : null;

        var error = await Assert.ThrowsAsync<SiloApiException>(() =>
            new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", "stale-token"), ct));

        Assert.Equal(ProblemCodes.ProfileVerificationRequired, error.Code);
        var stored = await _pipeline.Credentials.GetAsync(server.Id, ct);
        Assert.Equal(("7", "pin-token"), (stored?.ProfileId, stored?.ProfileToken));
    }

    [Fact]
    public async Task AHouseholdWriteAsTheActingProfile_IsNotSentOnceAnotherActs()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "9");

        await Assert.ThrowsAsync<ProfileChangedException>(() => new ProfilesApi(_pipeline.Http)
            .DeleteAsync(server.BaseUri, "8", ManagingProfile.Acting("1", "7"), TestContext.Current.CancellationToken));

        Assert.Empty(_pipeline.Server.Requests);
    }

    [Fact]
    public async Task ALentProfile_GoesOnlyOnItsOwnRequest_NotOnOnesSentWhileItIsInFlight()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token");
        var lentArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pipeline.Server.Hold = r =>
        {
            if (r.Path != "/api/v2/profiles/9")
            {
                return Task.CompletedTask;
            }
            lentArrived.TrySetResult();
            return release.Task;
        };
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/profiles/9" ? FakeSiloServer.Json(204, "") : null;

        var lent = new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", "owner-token"), ct);
        await lentArrived.Task.WaitAsync(ct);
        await new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct);
        await _pipeline.Http.GetAsync(server.BaseUri, "api/v2/home", SiloAuth.Profile, Core.Api.SiloJsonContext.Default.DictionaryStringString, ct);
        await new ProfilesApi(_pipeline.Http).ListAsync(server.BaseUri, ct);
        release.SetResult();
        await lent;

        Assert.Equal(("1", "owner-token"), Headers(_pipeline.Server.RequestsTo("/api/v2/profiles/9").Single()));
        Assert.Equal((null, null), Headers(_pipeline.Server.RequestsTo("/api/v2/account/me").Single()));
        Assert.Equal(("7", "pin-token"), Headers(_pipeline.Server.RequestsTo("/api/v2/home").Single()));
        Assert.Equal((null, null), Headers(_pipeline.Server.RequestsTo("/api/v2/profiles").Single()));
    }

    [Fact]
    public async Task ALentProfile_DoesNotOutliveItsRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/profiles/9" ? FakeSiloServer.Json(204, "") : null;

        await new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", "owner-token"), ct);
        await new ProfilesApi(_pipeline.Http).ListAsync(server.BaseUri, ct);
        await new AccountApi(_pipeline.Http).GetMeAsync(server.BaseUri, ct);

        Assert.All(_pipeline.Server.Requests.Where(r => r.Path != "/api/v2/profiles/9"), r => Assert.Equal((null, null), Headers(r)));
        Assert.Null((await _pipeline.Credentials.GetAsync(server.Id, ct))!.ProfileId);
    }

    [Fact]
    public async Task ALentProfile_IsReplayedAfterARefresh_ButNeverSentWithTheRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token");
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/profiles/9"
            ? r.Authorization == "Bearer access-1" ? FakeSiloServer.Problem(401, ProblemCodes.InvalidToken) : FakeSiloServer.Json(204, "")
            : null;

        await new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", "owner-token"), ct);

        Assert.Equal(["/api/v2/profiles/9", "/api/v2/auth/refresh", "/api/v2/profiles/9"], _pipeline.Server.Requests.Select(r => r.Path));
        Assert.All(_pipeline.Server.RequestsTo("/api/v2/profiles/9"), r => Assert.Equal(("1", "owner-token"), Headers(r)));
        var refresh = _pipeline.Server.RequestsTo("/api/v2/auth/refresh").Single();
        Assert.Equal((null, null), Headers(refresh));
        Assert.DoesNotContain("owner-token", refresh.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALentProfile_IsNotSentOnceAnotherAccountIsSignedIn()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsSomeoneElseAsync(server);

        await Assert.ThrowsAsync<ProfileChangedException>(() => new ProfilesApi(_pipeline.Http)
            .DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", "owner-token"), TestContext.Current.CancellationToken));

        Assert.Empty(_pipeline.Server.Requests);
    }

    [Fact]
    public async Task ALentProfile_IsNotSentAfterSigningOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        var session = await _pipeline.SignInAsync(server);
        await _pipeline.Credentials.RemoveAsync(server.Id, session.SessionId, ct);

        await Assert.ThrowsAsync<ProfileChangedException>(() => new ProfilesApi(_pipeline.Http)
            .DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", "owner-token"), ct));

        Assert.Empty(_pipeline.Server.Requests);
    }

    [Fact]
    public async Task ALentProfile_IsNotSentToAnotherServer_EvenOneTheSameAccountIsSignedInTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = await _pipeline.AddServerAsync("https://silo.test");
        var other = await _pipeline.Registry.AddOrUpdateAsync(new Uri("https://other.test"), "Other server", "server-2", ct);
        await _pipeline.SignInAsync(home);
        await _pipeline.SignInAsync(other);
        var lent = ManagingProfile.Lent(home.BaseUri, "1", "1", "owner-token");

        await Assert.ThrowsAsync<ProfileChangedException>(() => new ProfilesApi(_pipeline.Http).DeleteAsync(other.BaseUri, "9", lent, ct));
        await Assert.ThrowsAsync<ProfileChangedException>(() => new ProfilesApi(_pipeline.Http).DeleteAsync(new Uri("https://evil.test"), "9", lent, ct));
        await Assert.ThrowsAsync<ProfileChangedException>(() => new ProfilesApi(_pipeline.Http).DeleteAsync(new Uri("https://silo.test/elsewhere"), "9",
            ManagingProfile.Lent(new Uri("https://silo.test/elsewhere"), "1", "1", "owner-token"), ct));

        Assert.Empty(_pipeline.Server.Requests);
    }

    [Fact]
    public async Task ALentProfile_GoesOnlyOnAnAccountRequest()
    {
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7", profileToken: "pin-token");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseUri, "api/v2/home"));
        request.SetAuth(SiloAuth.Profile);
        request.SetServer(server.BaseUri);
        request.Options.Set(SiloRequestOptions.Lent, ManagingProfile.Lent(server.BaseUri, "1", "1", "owner-token"));
        using var client = new HttpClient(new Core.Http.AuthenticationHandler(_pipeline.Registry, _pipeline.Credentials, _pipeline.Refresher,
            _pipeline.Clock, _pipeline.Logs.For<Core.Http.AuthenticationHandler>()) { InnerHandler = _pipeline.Server });

        await Assert.ThrowsAsync<ProfileChangedException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(_pipeline.Server.Requests);
    }

    [Fact]
    public async Task ALentProfile_DoesNotFollowARedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server);
        _pipeline.Server.Intercept = r =>
        {
            if (r.Path != "/api/v2/profiles/9")
            {
                return null;
            }
            var moved = new HttpResponseMessage(System.Net.HttpStatusCode.TemporaryRedirect);
            moved.Headers.Location = new Uri("https://evil.test/collect");
            return moved;
        };

        await Assert.ThrowsAsync<SiloApiException>(() =>
            new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", ManagingProfile.Lent(server.BaseUri, "1", "1", "owner-token"), ct));

        Assert.Equal(["/api/v2/profiles/9"], _pipeline.Server.Requests.Select(r => r.Path));
        Assert.False(Core.CoreServiceCollectionExtensions.CreateSocketsHandler().AllowAutoRedirect);
    }

    [Fact]
    public async Task ALentProfilesIdAndProof_NeverReachTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.SignInAsync(server, profileId: "7");
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/profiles/9" ? FakeSiloServer.Problem(403, ProblemCodes.ProfileVerificationRequired) : null;
        var lent = ManagingProfile.Lent(server.BaseUri, "1", "p-lent-owner", "pvt-lent-secret");

        await Assert.ThrowsAsync<SiloApiException>(() => new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", lent, ct));
        await _pipeline.SignInAsSomeoneElseAsync(server);
        var refused = await Assert.ThrowsAsync<ProfileChangedException>(() => new ProfilesApi(_pipeline.Http).DeleteAsync(server.BaseUri, "9", lent, ct));

        Assert.NotEmpty(_pipeline.Logs.Lines);
        foreach (string secret in new[] { "p-lent-owner", "pvt-lent-secret" })
        {
            Assert.DoesNotContain(secret, _pipeline.Logs.All, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, refused.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, lent.ToString(), StringComparison.Ordinal);
        }
    }

    private static (string? Id, string? Token) Headers(Fakes.RecordedRequest request) => (request.Header("X-Profile-Id"), request.Header("X-Profile-Token"));

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
