using System.Text.RegularExpressions;

namespace Granary.Core.Diagnostics;

/// <summary>
/// Masks credentials in text before it is logged: bearer tokens, token fields in JSON or query
/// strings, and the credential headers Granary sends. Logs must never contain tokens.
/// </summary>
public static partial class LogRedactor
{
    private const string Mask = "<redacted>";

    [GeneratedRegex(@"(?i)(bearer\s+)[^\s,""']+")]
    private static partial Regex BearerValue();

    [GeneratedRegex(@"(?i)((?:access_token|refresh_token|profile_token|device_code|x-profile-token|password|\bst|\btoken)""?\s*[:=]\s*""?)[^\s,""&']+")]
    private static partial Regex SecretField();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        string masked = BearerValue().Replace(text, "$1" + Mask);
        return SecretField().Replace(masked, "$1" + Mask);
    }
}
