using System.Text.RegularExpressions;

namespace Granary.Core.Diagnostics;

/// <summary>
/// Masks credentials in text before it is logged: bearer tokens, token fields in JSON or query
/// strings, the credential headers Granary sends, and the secret of the media proxy's URLs.
/// Logs must never contain tokens.
/// </summary>
public static partial class LogRedactor
{
    private const string Mask = "<redacted>";

    [GeneratedRegex(@"(?i)(bearer\s+)[^\s,""']+")]
    private static partial Regex BearerValue();

    // JSON and query names (access_token) and record printouts (AccessToken = ...).
    [GeneratedRegex(@"(?i)((?:access_?token|refresh_?token|profile_?token|device_?code|x-profile-token|password|\bpin|\bst|\btoken)""?\s*[:=]\s*""?)[^\s,""&'}]+")]
    private static partial Regex SecretField();

    // A JWT anywhere, even without a field name next to it.
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    // MediaProxy URLs: /{secret}/{route}/..., which reach the server as the viewer while the route lives.
    [GeneratedRegex(@"/[0-9a-f]{64}/[0-9a-f]{32}/")]
    private static partial Regex ProxySecret();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        string masked = BearerValue().Replace(text, "$1" + Mask);
        masked = SecretField().Replace(masked, "$1" + Mask);
        masked = ProxySecret().Replace(masked, "/" + Mask + "/");
        return Jwt().Replace(masked, Mask);
    }
}
