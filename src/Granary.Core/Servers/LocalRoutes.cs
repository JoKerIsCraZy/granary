using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Granary.Core.Servers;

/// <summary>
/// A verified way to a saved server through the local network (docs/api-contract.md, 9.7): HTTPS to a private
/// address that is trusted only by the key the saved address vouched for. Requests for the saved address go here
/// instead; the saved address stays the credential owner.
/// </summary>
public sealed class LocalRoute : IDisposable
{
    // The path and query go on exactly as written (plan URLs are replayed byte for byte).
    private static readonly UriCreationOptions Verbatim = new() { DangerousDisablePathAndQueryCanonicalization = true };

    public LocalRoute(Uri savedOrigin, Uri origin, string pin, HttpMessageInvoker client)
    {
        SavedOrigin = new Uri(savedOrigin.GetLeftPart(UriPartial.Authority));
        Origin = new Uri(origin.GetLeftPart(UriPartial.Authority));
        Pin = pin;
        Client = client;
    }

    /// <summary>The saved address's origin whose requests this route takes.</summary>
    public Uri SavedOrigin { get; }

    /// <summary><c>https://{private IPv4}:{port}</c>.</summary>
    public Uri Origin { get; }

    /// <summary>The key's base64 SHA-256 the route was verified with.</summary>
    public string Pin { get; }

    /// <summary>Connects to <see cref="Origin"/> and accepts no certificate but the pinned key.</summary>
    public HttpMessageInvoker Client { get; }

    /// <summary>The same URL on this route.</summary>
    public Uri Rewrite(Uri uri)
    {
        string text = uri.OriginalString;
        int path = text.IndexOf('/', text.IndexOf("//", StringComparison.Ordinal) + 2);
        return new Uri(Origin.GetLeftPart(UriPartial.Authority) + (path < 0 ? "/" : text[path..]), Verbatim);
    }

    /// <summary>
    /// A handler that accepts exactly the key with <paramref name="pin"/>. The server makes its certificate anew in
    /// every process and for no name: its names and dates mean nothing, only the key counts (server-identity.md).
    /// </summary>
    public static SocketsHttpHandler PinnedHandler(string pin, TimeSpan connectTimeout) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = connectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && SpkiSha256(certificate) == pin,
        },
    };

    /// <summary>Base64 SHA-256 of the certificate's SubjectPublicKeyInfo, as <c>local_route.spki_sha256</c> names it.</summary>
    public static string SpkiSha256(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (certificate is X509Certificate2 full)
        {
            return Hash(full);
        }
        using var copy = new X509Certificate2(certificate);
        return Hash(copy);

        static string Hash(X509Certificate2 certificate) =>
            Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    public void Dispose() => Client.Dispose();
}

/// <summary>The local routes in use, by the origin of the saved address they stand in for.</summary>
public sealed class LocalRoutes : IDisposable
{
    private readonly ConcurrentDictionary<string, LocalRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The route that takes requests for <paramref name="uri"/>, if one is verified.</summary>
    public LocalRoute? Find(Uri uri) =>
        uri.IsAbsoluteUri && _routes.TryGetValue(uri.GetLeftPart(UriPartial.Authority), out var route) ? route : null;

    public void Use(LocalRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        string key = route.SavedOrigin.GetLeftPart(UriPartial.Authority);
        var before = _routes.TryGetValue(key, out var current) ? current : null;
        _routes[key] = route;
        if (before is not null && !ReferenceEquals(before, route))
        {
            before.Dispose();
        }
    }

    /// <summary>Stops using <paramref name="route"/>, unless another one took its place meanwhile.</summary>
    public void Drop(LocalRoute route)
    {
        if (_routes.TryRemove(new KeyValuePair<string, LocalRoute>(route.SavedOrigin.GetLeftPart(UriPartial.Authority), route)))
        {
            route.Dispose();
        }
    }

    /// <summary>Stops using whatever route stands in for <paramref name="saved"/>.</summary>
    public void Drop(Uri saved)
    {
        if (_routes.TryRemove(saved.GetLeftPart(UriPartial.Authority), out var route))
        {
            route.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var route in _routes.Values)
        {
            route.Dispose();
        }
        _routes.Clear();
    }
}
