using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.Http;
using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Auth;

/// <summary>The production handler chain (identity → authentication) in front of a <see cref="FakeSiloServer"/>.</summary>
internal sealed class AuthPipeline
{
    public static readonly ClientIdentity Identity = new("device-1", "TEST-PC", "windows", "Granary", "0.1.0", "dev", "desktop");

    public AuthPipeline()
    {
        Credentials = new CredentialStore(Secrets, Logs.For<CredentialStore>());
        Refresher = new TokenRefresher(Credentials, new RefreshClientFactory(Server), Clock, Logs.For<TokenRefresher>());
        var authentication = new AuthenticationHandler(Registry, Credentials, Refresher, Clock, Logs.For<AuthenticationHandler>())
        {
            InnerHandler = Server,
        };
        Http = new SiloHttp(new HttpClient(new ClientIdentityHandler(Identity) { InnerHandler = authentication }));
    }

    public FakeSiloServer Server { get; } = new();

    public InMemoryServerRegistry Registry { get; } = new();

    public InMemorySecretStore Secrets { get; } = new();

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    public LogSink Logs { get; } = new();

    public CredentialStore Credentials { get; }

    public TokenRefresher Refresher { get; }

    public SiloHttp Http { get; }

    public Task<ServerEntry> AddServerAsync(string url = "https://silo.test") =>
        Registry.AddOrUpdateAsync(new Uri(url), "Test server", "server-1");

    /// <summary>Stores the server's current tokens, as if signed in at <see cref="Clock"/>.</summary>
    public async Task<SessionCredentials> SignInAsync(ServerEntry server, string? profileId = null, string? profileToken = null, TimeSpan? expiresIn = null)
    {
        var lifetime = expiresIn ?? TimeSpan.FromHours(8);
        var account = new Account("1", "alex", "", "user", [], true, false, null);
        var credentials = new SessionCredentials(SessionCredentials.NewSessionId(), Server.AccessToken, Server.RefreshToken, Clock.Now + lifetime,
            (long)lifetime.TotalSeconds, profileId, profileToken, account);
        await Credentials.SetAsync(server.Id, credentials);
        return credentials;
    }

    /// <summary>A different login to the same server, as after signing out and in again (possibly as someone else).</summary>
    public async Task<SessionCredentials> SignInAsSomeoneElseAsync(ServerEntry server)
    {
        var credentials = new SessionCredentials(SessionCredentials.NewSessionId(), "bob-access", "bob-refresh", Clock.Now.AddHours(8), 28800,
            "9", null, new Account("2", "bob", "", "user", [], true, false, null));
        await Credentials.SetAsync(server.Id, credentials);
        return credentials;
    }

    public SessionManager CreateSessionManager() =>
        new(Registry, Credentials, Refresher, new AuthApi(Http), new AccountApi(Http), Clock, Logs.For<SessionManager>());

    /// <summary>Waits up to two seconds for work that finishes on another thread.</summary>
    public static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "The condition was not met within two seconds.");
    }

    public static async Task Eventually(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 200 && !await condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(await condition(), "The condition was not met within two seconds.");
    }

    /// <summary>Every token the fake server has issued so far; none of them may appear in a log line.</summary>
    public IEnumerable<string> IssuedSecrets(int generations = 10) =>
        Enumerable.Range(1, generations).SelectMany(g => new[] { $"access-{g}", $"refresh-{g}" });

    private sealed class RefreshClientFactory(FakeSiloServer server) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new ClientIdentityHandler(Identity) { InnerHandler = server }, disposeHandler: false);
    }
}
