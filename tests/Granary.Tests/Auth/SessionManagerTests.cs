using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Auth;
using Granary.Tests.Fakes;

namespace Granary.Tests.Auth;

public sealed class SessionManagerTests
{
    private readonly AuthPipeline _pipeline = new();

    [Fact]
    public async Task Initialize_WithoutServers_IsNoServer()
    {
        using var session = _pipeline.CreateSessionManager();

        await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.NoServer, session.State);
        Assert.Null(session.ActiveServer);
    }

    [Fact]
    public async Task PasswordSignIn_StoresTheSessionAndAsksForAProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        int changes = 0;
        session.Changed += (_, _) => Interlocked.Increment(ref changes);

        var account = await session.SignInWithPasswordAsync(server, "alex", "correct horse", ct);

        Assert.Equal("alex", account.Username);
        Assert.Equal(SessionState.ProfileRequired, session.State);
        Assert.Equal(server.Id, session.ActiveServer!.Id);
        Assert.Equal(server.Id, _pipeline.Registry.ActiveServerId);
        // Changed may come from the background sync that saw the new credentials first.
        await AuthPipeline.Eventually(() => Volatile.Read(ref changes) > 0);
        var stored = await _pipeline.Credentials.GetAsync(server.Id, ct);
        Assert.Equal("access-1", stored!.AccessToken);
        Assert.Equal(_pipeline.Clock.Now.AddHours(8), stored.AccessTokenExpiresAt);
        var login = _pipeline.Server.RequestsTo("/api/v2/auth/login").Single();
        Assert.Null(login.Authorization);
        Assert.Equal("device-1", login.Header("X-Silo-Device-Id"));
        Assert.Equal("""{"username":"alex","password":"correct horse"}""", login.Body);
    }

    [Fact]
    public async Task WrongPassword_LeavesTheSessionSignedOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/auth/login" ? FakeSiloServer.Problem(401, ProblemCodes.InvalidToken) : null;
        using var session = _pipeline.CreateSessionManager();

        var error = await Assert.ThrowsAsync<SiloApiException>(() => session.SignInWithPasswordAsync(server, "alex", "wrong", ct));

        Assert.Equal(401, error.Status);
        Assert.Equal(0, _pipeline.Server.RefreshCalls);
        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
        Assert.DoesNotContain("wrong", _pipeline.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingAProfile_MakesTheSessionReadyAndIsRemembered()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);

        await session.SelectProfileAsync(server.Id, "7", "pin-token", ct);

        Assert.Equal(SessionState.Ready, session.State);
        Assert.Equal("7", session.ProfileId);
        Assert.Equal("7", _pipeline.Registry.Find(server.Id)!.LastProfileId);
        Assert.Equal("pin-token", (await _pipeline.Credentials.GetAsync(server.Id, ct))!.ProfileToken);
    }

    [Fact]
    public async Task APickForAServerNoLongerActive_IsNotApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _pipeline.AddServerAsync();
        var second = await _pipeline.AddServerAsync("https://other.test");
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(first, "alex", "pw", ct);
        await session.SignInWithPasswordAsync(second, "alex", "pw", ct);

        await Assert.ThrowsAsync<ActiveServerChangedException>(() => session.SelectProfileAsync(first.Id, "7", null, ct));

        Assert.Equal(second.Id, session.ActiveServer!.Id);
        Assert.Equal(SessionState.ProfileRequired, session.State);
        Assert.Null((await _pipeline.Credentials.GetAsync(first.Id, ct))!.ProfileId);
        Assert.Null((await _pipeline.Credentials.GetAsync(second.Id, ct))!.ProfileId);
        Assert.Null(_pipeline.Registry.Find(first.Id)!.LastProfileId);
    }

    [Fact]
    public async Task ReopeningASignedInServer_MakesItActive_AndAsksWhoIsWatching()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        var other = await _pipeline.AddServerAsync("https://other.test");
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        await session.SelectProfileAsync(server.Id, "7", "pin-token", ct);
        await session.SignInWithPasswordAsync(other, "sam", "pw", ct);

        bool reopened = await session.ReopenServerAsync(server.Id, "server-1", ct);

        Assert.True(reopened);
        Assert.Equal(server.Id, session.ActiveServer!.Id);
        Assert.Equal(SessionState.ProfileRequired, session.State);
        var stored = await _pipeline.Credentials.GetAsync(server.Id, ct);
        Assert.Null(stored!.ProfileId);
        Assert.Null(stored.ProfileToken);
        Assert.Equal("access-1", stored.AccessToken);
    }

    [Fact]
    public async Task ReopeningAnAddressThatAnswersAsAnotherServer_ForgetsItsSession_WithoutTellingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        await session.SelectProfileAsync(server.Id, "7", null, ct);

        bool reopened = await session.ReopenServerAsync(server.Id, "server-2", ct);

        Assert.False(reopened);
        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
        Assert.Empty(_pipeline.Server.RequestsTo("/api/v2/auth/logout"));
        Assert.Equal(SessionState.SignedOut, session.State);
    }

    [Fact]
    public async Task ReopeningAnAddressThatGivesNoIdentity_KeepsTheSession_ButDoesNotReopenIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        await session.SelectProfileAsync(server.Id, "7", "pin-token", ct);

        bool reopened = await session.ReopenServerAsync(server.Id, serverId: null, ct);

        Assert.False(reopened);
        var stored = await _pipeline.Credentials.GetAsync(server.Id, ct);
        Assert.Equal("7", stored!.ProfileId);
        Assert.Equal("pin-token", stored.ProfileToken);
    }

    [Fact]
    public async Task ReopeningASignedOutServer_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        var signedOut = await _pipeline.AddServerAsync("https://other.test");
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);

        bool reopened = await session.ReopenServerAsync(signedOut.Id, "server-1", ct);

        Assert.False(reopened);
        Assert.Equal(server.Id, session.ActiveServer!.Id);
        Assert.Null(await _pipeline.Credentials.GetAsync(signedOut.Id, ct));
    }

    [Fact]
    public async Task Restart_RestoresTheReadySession()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using (var first = _pipeline.CreateSessionManager())
        {
            await first.SignInWithPasswordAsync(server, "alex", "pw", ct);
            await first.SelectProfileAsync(server.Id, "7", null, ct);
        }

        using var second = _pipeline.CreateSessionManager();
        await second.InitializeAsync(ct);

        Assert.Equal(SessionState.Ready, second.State);
        Assert.Equal("alex", second.Account!.Username);
        Assert.Equal("7", second.ProfileId);
    }

    [Fact]
    public async Task ClearProfile_GoesBackToTheProfilePicker()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        await session.SelectProfileAsync(server.Id, "7", "pin-token", ct);

        await session.ClearProfileAsync(ct);

        Assert.Equal(SessionState.ProfileRequired, session.State);
        Assert.Null((await _pipeline.Credentials.GetAsync(server.Id, ct))!.ProfileToken);
    }

    [Fact]
    public async Task SignOut_TellsTheServerAndForgetsTheTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);

        await session.SignOutAsync(cancellationToken: ct);

        Assert.Equal(SessionState.SignedOut, session.State);
        Assert.Equal(server.Id, session.ActiveServer!.Id);
        Assert.Null(session.Account);
        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
        Assert.Equal("Bearer access-1", _pipeline.Server.RequestsTo("/api/v2/auth/logout").Single().Authorization);
    }

    [Fact]
    public async Task SignOut_ForgetsTheTokensEvenWhenTheServerFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/auth/logout" ? FakeSiloServer.Problem(500, "internal") : null;

        await session.SignOutAsync(server.Id, ct);

        Assert.Equal(SessionState.SignedOut, session.State);
        Assert.Null(await _pipeline.Credentials.GetAsync(server.Id, ct));
    }

    [Fact]
    public async Task SignOutOfAnotherServer_KeepsTheActiveSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = await _pipeline.AddServerAsync("https://home.test");
        var cabin = await _pipeline.AddServerAsync("https://cabin.test");
        await _pipeline.SignInAsync(cabin);
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(home, "alex", "pw", ct);

        await session.SignOutAsync(cabin.Id, ct);

        Assert.Equal(SessionState.ProfileRequired, session.State);
        Assert.Equal(home.Id, session.ActiveServer!.Id);
        Assert.Null(await _pipeline.Credentials.GetAsync(cabin.Id, ct));
        Assert.NotNull(await _pipeline.Credentials.GetAsync(home.Id, ct));
    }

    [Fact]
    public async Task ServerEndingTheSession_SignsOutAndRaisesChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        await session.SelectProfileAsync(server.Id, "7", null, ct);
        var signedOut = new TaskCompletionSource();
        session.Changed += (_, _) =>
        {
            if (session.State == SessionState.SignedOut)
            {
                signedOut.TrySetResult();
            }
        };
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/home" ? FakeSiloServer.Problem(401, ProblemCodes.SessionExpired) : null;

        await Assert.ThrowsAsync<SiloApiException>(() =>
            _pipeline.Http.GetAsync(server.BaseUri, "api/v2/home", SiloAuth.Profile, SiloJsonContext.Default.DictionaryStringString, ct));

        await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Null(session.ProfileId);
    }

    [Fact]
    public async Task ProfileRejectedByTheServer_ReturnsToTheProfilePicker()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(server, "alex", "pw", ct);
        await session.SelectProfileAsync(server.Id, "7", "stale", ct);
        _pipeline.Server.Intercept = r => r.Path == "/api/v2/home" ? FakeSiloServer.Problem(403, ProblemCodes.ProfileVerificationRequired) : null;

        await Assert.ThrowsAsync<SiloApiException>(() =>
            _pipeline.Http.GetAsync(server.BaseUri, "api/v2/home", SiloAuth.Profile, SiloJsonContext.Default.DictionaryStringString, ct));

        await AuthPipeline.Eventually(() => session.State == SessionState.ProfileRequired);
    }

    [Fact]
    public async Task TemporaryPassword_RequiresAChangeUntilTheAccountIsRefreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        _pipeline.Server.PasswordChangeRequired = true;
        using var session = _pipeline.CreateSessionManager();

        await session.SignInWithPasswordAsync(server, "alex", "temporary", ct);
        Assert.Equal(SessionState.PasswordChangeRequired, session.State);

        await new AccountApi(_pipeline.Http).ChangePasswordAsync(server.BaseUri, "temporary", "a much better one", ct);
        await session.RefreshAccountAsync(ct);

        Assert.Equal(SessionState.ProfileRequired, session.State);
        Assert.False(session.Account!.PasswordChangeRequired);
        // New tokens drop the restriction, so the refresh comes before reading the account.
        Assert.Equal(1, _pipeline.Server.RefreshCalls);
        Assert.Equal("Bearer access-2", _pipeline.Server.RequestsTo("/api/v2/account/me").Last().Authorization);
    }

    [Fact]
    public async Task SwitchingServers_LoadsThatServersSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = await _pipeline.AddServerAsync("https://home.test");
        var cabin = await _pipeline.AddServerAsync("https://cabin.test");
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(home, "alex", "pw", ct);
        await session.SelectProfileAsync(home.Id, "7", null, ct);

        await session.ActivateServerAsync(cabin.Id, ct);
        Assert.Equal(SessionState.SignedOut, session.State);
        Assert.Equal(cabin.Id, session.ActiveServer!.Id);

        await session.ActivateServerAsync(home.Id, ct);
        Assert.Equal(SessionState.Ready, session.State);
        Assert.Equal("7", session.ProfileId);
    }

    [Fact]
    public async Task RemovingTheActiveServer_FollowsTheRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = await _pipeline.AddServerAsync("https://home.test");
        var cabin = await _pipeline.AddServerAsync("https://cabin.test");
        using var session = _pipeline.CreateSessionManager();
        await session.SignInWithPasswordAsync(cabin, "alex", "pw", ct);
        await session.SignInWithPasswordAsync(home, "alex", "pw", ct);

        await session.SignOutAsync(home.Id, ct);
        await _pipeline.Registry.RemoveAsync(home.Id, ct);

        await AuthPipeline.Eventually(() => session.ActiveServer?.Id == cabin.Id);
        Assert.Equal(SessionState.ProfileRequired, session.State);
    }

    [Fact]
    public async Task SignInToAnUnsavedServer_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = await _pipeline.AddServerAsync();
        await _pipeline.Registry.RemoveAsync(server.Id, ct);
        using var session = _pipeline.CreateSessionManager();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SignInWithPasswordAsync(server, "alex", "pw", ct));
    }
}
