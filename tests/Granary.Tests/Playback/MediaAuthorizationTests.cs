using Granary.Core.Playback;
using Granary.Tests.Auth;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

public sealed class MediaAuthorizationTests
{
    private readonly AuthPipeline _auth = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private MediaAuthorization Create() => new(_auth.Credentials, _auth.Refresher, _auth.Clock, _auth.Logs.For<MediaAuthorization>());

    [Fact]
    public async Task Headers_CarryTheBearerAndTheActingProfile()
    {
        var server = await _auth.AddServerAsync();
        await _auth.SignInAsync(server, profileId: "7", profileToken: "pin-proof");

        var media = await Create().GetAsync(server, rejected: false, Token);

        Assert.Equal(
            [new("Authorization", "Bearer access-1"), new("X-Profile-Id", "7"), new("X-Profile-Token", "pin-proof")],
            media.Headers);
        Assert.Equal(_auth.Clock.Now.AddHours(8), media.ExpiresAt);
        Assert.Equal(0, _auth.Server.RefreshCalls);
    }

    [Fact]
    public async Task AProfileWithoutPin_SendsNoProfileToken()
    {
        var server = await _auth.AddServerAsync();
        await _auth.SignInAsync(server, profileId: "7");

        var media = await Create().GetAsync(server, rejected: false, Token);

        Assert.DoesNotContain(media.Headers, h => h.Key == "X-Profile-Token");
    }

    [Fact]
    public async Task ATokenPastHalfItsLifetime_IsRefreshedBeforeTheLoad()
    {
        // mpv keeps the headers of a load for the whole stream: give it a token with most of its life ahead.
        var server = await _auth.AddServerAsync();
        await _auth.SignInAsync(server, profileId: "7");
        _auth.Clock.Now = _auth.Clock.Now.AddHours(5);

        var media = await Create().GetAsync(server, rejected: false, Token);

        Assert.Equal(1, _auth.Server.RefreshCalls);
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer access-2"), media.Headers);
    }

    [Fact]
    public async Task RejectedHeaders_AreRefreshedEvenWhenTheTokenLooksFresh()
    {
        var server = await _auth.AddServerAsync();
        await _auth.SignInAsync(server, profileId: "7");

        var media = await Create().GetAsync(server, rejected: true, Token);

        Assert.Equal(1, _auth.Server.RefreshCalls);
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer access-2"), media.Headers);
    }

    [Fact]
    public async Task AFailedRefresh_KeepsATokenThatIsStillValid()
    {
        var server = await _auth.AddServerAsync();
        await _auth.SignInAsync(server, profileId: "7");
        _auth.Clock.Now = _auth.Clock.Now.AddHours(5);
        _auth.Server.Intercept = r => r.Path == "/api/v2/auth/refresh" ? FakeSiloServer.Problem(503, "dependency_unavailable") : null;

        var media = await Create().GetAsync(server, rejected: false, Token);

        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer access-1"), media.Headers);
    }

    [Fact]
    public async Task AFailedRefresh_AfterARejection_CannotAuthorize()
    {
        var server = await _auth.AddServerAsync();
        await _auth.SignInAsync(server, profileId: "7");
        _auth.Server.Intercept = r => r.Path == "/api/v2/auth/refresh" ? FakeSiloServer.Problem(503, "dependency_unavailable") : null;

        var failure = await Assert.ThrowsAsync<MediaAuthorizationException>(() => Create().GetAsync(server, rejected: true, Token));

        Assert.Equal(MediaAuthorizationFailure.Unavailable, failure.Failure);
    }

    [Fact]
    public async Task WithoutALogin_NothingCanBePlayed()
    {
        var server = await _auth.AddServerAsync();

        var failure = await Assert.ThrowsAsync<MediaAuthorizationException>(() => Create().GetAsync(server, rejected: false, Token));

        Assert.Equal(MediaAuthorizationFailure.SignedOut, failure.Failure);
    }

    [Fact]
    public async Task WithoutAProfile_NothingCanBePlayed()
    {
        var server = await _auth.AddServerAsync();
        await _auth.SignInAsync(server);

        var failure = await Assert.ThrowsAsync<MediaAuthorizationException>(() => Create().GetAsync(server, rejected: false, Token));

        Assert.Equal(MediaAuthorizationFailure.NoProfile, failure.Failure);
    }

    [Fact]
    public async Task TheHeaders_NeverShowUpInTheLog()
    {
        var server = await _auth.AddServerAsync();
        await _auth.SignInAsync(server, profileId: "7", profileToken: "pin-proof");

        await Create().GetAsync(server, rejected: true, Token);

        Assert.DoesNotContain("access-", _auth.Logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("pin-proof", _auth.Logs.All, StringComparison.Ordinal);
    }
}
