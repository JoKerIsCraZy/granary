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

    /// <summary>The server base URL the request was built under. Handlers send credentials only beneath it.</summary>
    public static readonly HttpRequestOptionsKey<Uri> Server = new("granary.server");

    /// <summary>The request must go to the saved address itself, never over a local route (the key it vouches for).</summary>
    public static readonly HttpRequestOptionsKey<bool> SavedAddressOnly = new("granary.saved-address-only");

    public static SiloAuth GetAuth(this HttpRequestMessage request) =>
        request.Options.TryGetValue(Auth, out var auth) ? auth : SiloAuth.Anonymous;

    public static void SetAuth(this HttpRequestMessage request, SiloAuth auth) => request.Options.Set(Auth, auth);

    /// <summary>
    /// The server the request is addressed to, or null when it was not built by <see cref="SiloHttp"/> or
    /// its URL is not beneath that server's base URL.
    /// </summary>
    public static Uri? GetServer(this HttpRequestMessage request) =>
        request.Options.TryGetValue(Server, out var server) && request.RequestUri is { } uri && IsBeneath(uri, server)
            ? server
            : null;

    public static void SetServer(this HttpRequestMessage request, Uri server) => request.Options.Set(Server, server);

    internal static bool IsBeneath(Uri uri, Uri server) =>
        uri.IsAbsoluteUri
        && Uri.Compare(uri, server, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
        && uri.AbsolutePath.StartsWith(server.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal);
}
