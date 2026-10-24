namespace Granary.Core.Api.Profiles;

/// <summary>Household profiles (docs/api-contract.md, 6). Both calls run without profile headers.</summary>
public sealed class ProfilesApi(SiloHttp http)
{
    public Task<ProfileCollection> ListAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/profiles", SiloAuth.Account, SiloJsonContext.Default.ProfileCollection, cancellationToken);

    /// <summary>
    /// Checks a PIN. A wrong PIN is <c>Valid = false</c>; five failures lock the profile for five minutes
    /// (429 with Retry-After). Each call counts, so it is never retried automatically.
    /// </summary>
    public Task<ProfileVerification> VerifyPinAsync(Uri server, string profileId, string pin, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, $"api/v2/profiles/{SiloHttp.Escape(profileId)}/verify-pin", SiloAuth.Account, new ProfilePinCheck(pin),
            SiloJsonContext.Default.ProfilePinCheck, SiloJsonContext.Default.ProfileVerification, cancellationToken);
}
