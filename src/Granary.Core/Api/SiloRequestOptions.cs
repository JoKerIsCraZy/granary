namespace Granary.Core.Api;

/// <summary>Which credentials a request carries. The authentication handler reads it from the request options.</summary>
public enum SiloAuth
{
    /// <summary>No credentials (discovery, login, refresh, device sign-in).</summary>
    Anonymous,

    /// <summary>Bearer token only, no profile headers (account operations, profile list, verify-pin).</summary>
    Account,

    /// <summary>Bearer token plus the selected profile's <c>X-Profile-Id</c> / <c>X-Profile-Token</c>.</summary>
    Profile,
}

public static class SiloRequestOptions
{
    public static readonly HttpRequestOptionsKey<SiloAuth> Auth = new("granary.auth");

    public static SiloAuth GetAuth(this HttpRequestMessage request) =>
        request.Options.TryGetValue(Auth, out var auth) ? auth : SiloAuth.Anonymous;

    public static void SetAuth(this HttpRequestMessage request, SiloAuth auth) => request.Options.Set(Auth, auth);
}
