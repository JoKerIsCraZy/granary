namespace Granary.Core.Api.Auth;

/// <summary>Sign-in and session operations (docs/api-contract.md, 4 and 5).</summary>
public sealed class AuthApi(SiloHttp http)
{
    /// <summary>Password sign-in. Non-retryable: never replay after an uncertain outcome.</summary>
    public Task<TokenPair> LoginAsync(Uri server, string username, string password, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, "api/v2/auth/login", SiloAuth.Anonymous, new LoginRequest(username, password),
            SiloJsonContext.Default.LoginRequest, SiloJsonContext.Default.TokenPair, cancellationToken);

    public Task<RefreshedTokens> RefreshAsync(Uri server, string refreshToken, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, "api/v2/auth/refresh", SiloAuth.Anonymous, new RefreshRequest(refreshToken),
            SiloJsonContext.Default.RefreshRequest, SiloJsonContext.Default.RefreshedTokens, cancellationToken);

    /// <summary>Revokes the current login session (and with it its profile tokens).</summary>
    public Task LogoutAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Post, server, "api/v2/auth/logout", SiloAuth.Account, cancellationToken);

    public Task<AuthProviderCollection> GetProvidersAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/auth/providers", SiloAuth.Anonymous, SiloJsonContext.Default.AuthProviderCollection, cancellationToken);
}
