using Granary.Core.Api.Auth;

namespace Granary.Core.Api.Accounts;

/// <summary>The signed-in login account (docs/api-contract.md, 4.1).</summary>
public sealed class AccountApi(SiloHttp http)
{
    public Task<Account> GetMeAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/account/me", SiloAuth.Account, SiloJsonContext.Default.Account, cancellationToken);

    public Task<AccountPasswordCapability> GetPasswordCapabilityAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/account/password/capability", SiloAuth.Account, SiloJsonContext.Default.AccountPasswordCapability, cancellationToken);

    /// <summary>Replaces the password (204). Afterwards refresh the tokens to drop a temporary-password restriction.</summary>
    public Task ChangePasswordAsync(Uri server, string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Post, server, "api/v2/account/password", SiloAuth.Account, new ChangePasswordRequest(currentPassword, newPassword),
            SiloJsonContext.Default.ChangePasswordRequest, cancellationToken);
}
