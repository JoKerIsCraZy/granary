using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Auth;

/// <summary>
/// What the signed-in account may do beyond watching, as Silo's web decides it (lib/permissions.ts): an admin acting on
/// the household's primary profile, or with none selected, may do everything; otherwise only what the account was
/// given. The server checks again; this only decides what the app offers.
/// </summary>
public sealed class AccountPermissions(ISessionManager session, ProfilesApi profiles, ILogger<AccountPermissions> logger)
{
    /// <summary>Editing a file's intro, recap, credits and preview markers.</summary>
    public const string MarkerEdit = "marker_edit";

    public async Task<bool> HasAsync(string permission, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        if (session.Account is not { } account || session.ActiveServer is not { } server)
        {
            return false;
        }
        if (account.Permissions.Contains(permission, StringComparer.Ordinal))
        {
            return true;
        }
        return account.IsAdmin && await ActsOnPrimaryProfileAsync(server.BaseUri, session.ProfileId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An administrator acting on the primary profile (or none), as the web's <c>useIsActingAdmin</c>.</summary>
    public async Task<bool> IsActingAdminAsync(CancellationToken cancellationToken = default) =>
        session is { Account.IsAdmin: true, ActiveServer: { } server }
        && await ActsOnPrimaryProfileAsync(server.BaseUri, session.ProfileId, cancellationToken).ConfigureAwait(false);

    // An admin on another profile of the household (a child's) is a viewer there.
    private async Task<bool> ActsOnPrimaryProfileAsync(Uri server, string? profileId, CancellationToken cancellationToken)
    {
        if (profileId is null)
        {
            return true;
        }
        try
        {
            var list = await profiles.ListAsync(server, cancellationToken).ConfigureAwait(false);
            return list.Items.FirstOrDefault(p => p.Id == profileId)?.IsPrimary == true;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("The profiles could not be read to check a permission: {Failure}", ApiFailureText.Describe(ex));
            return false;
        }
    }
}
