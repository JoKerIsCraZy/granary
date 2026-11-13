using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Granary.Core.Remote;

/// <summary>
/// The id Silo's phones key a server by, and how they compare two of them (silo-android's <c>AndroidServerRegistry</c>:
/// <c>idFor</c>, <c>urlForServerId</c>, <c>serverIdsMatch</c>, <c>serversMatch</c>; silo-apple's <c>ServerRegistry</c>).
/// The id is the server's address: base64url without padding of the address with trailing slashes trimmed. It is not
/// opaque, so it is as private as the address.
/// </summary>
public static class SiloCastServerId
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The id of the server Granary is signed in to: its base address as <see cref="Uri.AbsoluteUri"/> spells it (scheme
    /// and host in lower case, no default port), without trailing slashes.
    /// </summary>
    public static string From(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("A server's address is absolute.", nameof(baseAddress));
        }
        return Encode(baseAddress.AbsoluteUri.TrimEnd('/'));
    }

    /// <summary>
    /// The http or https address an id names, or null when the id is anything else: an id is read as an address only when
    /// the address encodes back to exactly that id, so an id of another form is never taken for one.
    /// </summary>
    public static string? AddressOf(string id)
    {
        if (string.IsNullOrEmpty(id) || !Base64Url.IsValid(id, out _))
        {
            return null;
        }
        string address;
        try
        {
            address = StrictUtf8.GetString(Base64Url.DecodeFromChars(id));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
        string normalized = Trimmed(address);
        return Comparable(normalized) is not null && Encode(normalized) == id ? normalized : null;
    }

    /// <summary>
    /// Two ids name the same address: they are equal, or both are http or https addresses that are equal once scheme and
    /// host are in lower case and a default port is dropped. Credentials, path, query, fragment and any other port count
    /// as they are. A missing or empty id matches nothing.
    /// </summary>
    public static bool IdsMatch(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }
        return AddressOf(left) is { } leftAddress && AddressOf(right) is { } rightAddress
            && Comparable(leftAddress) is { } leftComparable
            && string.Equals(leftComparable, Comparable(rightAddress), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two servers are one deployment: their ids match, or both sides know the same deployment identity (the
    /// <c>server_id</c> of <c>GET /api/v2/system/identity</c>). A blank identity matches nothing.
    /// </summary>
    public static bool ServersMatch(string? leftId, string? leftIdentity, string? rightId, string? rightIdentity)
    {
        if (IdsMatch(leftId, rightId))
        {
            return true;
        }
        string? left = leftIdentity?.Trim();
        string? right = rightIdentity?.Trim();
        return !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string Encode(string address) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(address));

    private static string Trimmed(string address) => address.Trim().TrimEnd('/');

    /// <summary>
    /// <c>scheme://[userinfo@]host[:port]</c> with scheme and host in lower case and a default port dropped, then the
    /// rest as it is; null for anything that is not an http or https address (Android's <c>canonicalComparisonUrl</c>).
    /// </summary>
    private static string? Comparable(string address)
    {
        if (!IsUriText(address))
        {
            return null;
        }
        int schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return null;
        }
        string scheme = address[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            return null;
        }
        string rest = address[(schemeEnd + 3)..];
        int authorityEnd = rest.IndexOfAny(['/', '?', '#']);
        string authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        string tail = authorityEnd < 0 ? "" : rest[authorityEnd..];
        return ComparableAuthority(authority, scheme) is { } comparable ? $"{scheme}://{comparable}{tail}" : null;
    }

    // Android's canonicalAuthority: credentials as they are, the host in lower case, a default port dropped.
    private static string? ComparableAuthority(string authority, string scheme)
    {
        int at = authority.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0 && authority.IndexOf('@', at + 1) >= 0)
        {
            return null;
        }
        string userInfo = at < 0 ? "" : authority[..(at + 1)];
        string hostAndPort = authority[userInfo.Length..];
        if (hostAndPort.Length == 0 || SplitHost(hostAndPort) is not { } split)
        {
            return null;
        }
        (string host, string? port) = split;
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace))
        {
            return null;
        }
        int? number = null;
        if (port is not null)
        {
            if (port.Length == 0 || !port.All(char.IsAsciiDigit)
                || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed > 65535)
            {
                return null;
            }
            number = parsed;
        }
        bool isDefaultPort = number == (scheme == "http" ? 80 : 443);
        return $"{userInfo}{host.ToLowerInvariant()}{(port is null || isDefaultPort ? "" : ":" + port)}";
    }

    // The host and the port's text, an IPv6 literal in brackets; null when the two cannot be told apart.
    private static (string Host, string? Port)? SplitHost(string hostAndPort)
    {
        if (hostAndPort.StartsWith('['))
        {
            int bracket = hostAndPort.IndexOf(']', StringComparison.Ordinal);
            if (bracket <= 1)
            {
                return null;
            }
            string remainder = hostAndPort[(bracket + 1)..];
            if (remainder.Length == 0)
            {
                return (hostAndPort[..(bracket + 1)], null);
            }
            return remainder.StartsWith(':') ? (hostAndPort[..(bracket + 1)], remainder[1..]) : null;
        }
        if (hostAndPort.Contains('[', StringComparison.Ordinal) || hostAndPort.Contains(']', StringComparison.Ordinal))
        {
            return null;
        }
        int colon = hostAndPort.LastIndexOf(':');
        if (colon < 0)
        {
            return (hostAndPort, null);
        }
        string host = hostAndPort[..colon];
        return host.Contains(':', StringComparison.Ordinal) ? null : (host, hostAndPort[(colon + 1)..]);
    }

    // What java.net.URI refuses outright, so an address Android cannot read matches nothing here either: spaces, control
    // characters, the characters RFC 3986 never allows, and a '%' that is not an escape.
    private static bool IsUriText(string address)
    {
        for (int i = 0; i < address.Length; i++)
        {
            char c = address[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '<' or '>' or '\\' or '^' or '`' or '{' or '|' or '}')
            {
                return false;
            }
            if (c == '%' && (i + 2 >= address.Length || !char.IsAsciiHexDigit(address[i + 1]) || !char.IsAsciiHexDigit(address[i + 2])))
            {
                return false;
            }
        }
        return true;
    }
}
