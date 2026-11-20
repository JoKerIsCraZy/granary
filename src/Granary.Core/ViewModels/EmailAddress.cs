using System.Net;
using System.Text.RegularExpressions;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>
/// Whether a typed address looks like one real mailbox, as the web's <c>lib/email.ts</c>: something before a single "@",
/// and a domain with a dot that has text on both sides, or a bracketed IP address. It catches the usual slips before
/// the server is asked; the server stays the judge.
/// </summary>
public static partial class EmailAddress
{
    /// <summary>The one message every email field shows for a malformed address.</summary>
    public static string InvalidText => AppText.Get("web.auth.invalidEmail");

    public static bool IsValid(string value)
    {
        string trimmed = value.Trim();
        int at = trimmed.LastIndexOf('@');
        if (at <= 0 || at != trimmed.IndexOf('@') || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }
        string domain = trimmed[(at + 1)..];
        if (domain.StartsWith('['))
        {
            return IsLiteral(domain);
        }
        int dot = domain.LastIndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }

    // A dotted IPv4 or a bare IPv6 address in brackets, as Go's net/mail takes it.
    private static bool IsLiteral(string domain)
    {
        if (!domain.EndsWith(']'))
        {
            return false;
        }
        string inner = domain[1..^1];
        return Ipv4().IsMatch(inner) || (inner.Length > 1 && Ipv6().IsMatch(inner) && IPAddress.TryParse(inner, out _));
    }

    [GeneratedRegex(@"\A(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\z")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"\A[0-9a-fA-F:]*:[0-9a-fA-F:.]*\z")]
    private static partial Regex Ipv6();
}
