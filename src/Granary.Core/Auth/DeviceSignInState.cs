namespace Granary.Core.Auth;

/// <summary>Where a <see cref="DeviceSignInFlow"/> stands.</summary>
public enum DeviceSignInStatus
{
    /// <summary>Not running: never started, or stopped.</summary>
    Idle,

    /// <summary>Checking the server and asking it for a code.</summary>
    GettingCode,

    /// <summary>A code is shown and nobody has opened it yet.</summary>
    WaitingForApproval,

    /// <summary>Someone opened the code on another device ("continue on your phone").</summary>
    Opened,

    /// <summary>Calls keep failing with network errors or 5xx. Still retrying.</summary>
    Unreachable,

    /// <summary>The server answers 429. Still retrying.</summary>
    TooManyRequests,

    /// <summary>Signed in: the session manager holds the tokens.</summary>
    Approved,

    /// <summary>The approver declined.</summary>
    Denied,

    /// <summary>The server does not offer device sign-in; only a password works.</summary>
    Unavailable,

    /// <summary>The server answered in a way retrying will not fix.</summary>
    Failed,
}

/// <summary>
/// What the screen shows for one live code. The device code stays inside the flow; the user code is
/// for the screen only and must not be logged.
/// </summary>
/// <param name="UserCode">Grouped for display, for example <c>4821 7730</c>.</param>
/// <param name="VerificationText">The address to type: host and path, without scheme.</param>
/// <param name="QrPayload">The complete verification URI, which already carries the code.</param>
/// <param name="ExpiresAt">When the flow gives up on this code and asks for a new one (this PC's clock).</param>
public sealed record DeviceSignInCode(string UserCode, string VerificationText, string QrPayload, DateTimeOffset ExpiresAt)
{
    private const int GroupedLength = 8;

    /// <summary><c>4821-7730</c> → <c>4821 7730</c>. Anything that is not eight digits is shown as sent.</summary>
    public static string FormatUserCode(string userCode)
    {
        string trimmed = userCode.Trim();
        string compact = string.Concat(trimmed.Where(c => c != '-' && !char.IsWhiteSpace(c)));
        return compact.Length == GroupedLength && compact.All(char.IsAsciiDigit)
            ? $"{compact[..4]} {compact[4..]}"
            : trimmed;
    }

    /// <summary>
    /// <c>https://silo.example.org/activate</c> → <c>silo.example.org/activate</c> (host, port and path).
    /// Falls back to the complete URI without its query when the plain one is missing.
    /// </summary>
    public static string FormatVerificationText(string verificationUri, string verificationUriComplete)
    {
        string raw = string.IsNullOrWhiteSpace(verificationUri) ? verificationUriComplete.Split('?')[0] : verificationUri;
        raw = raw.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return raw.TrimEnd('/');
        }
        return (uri.Authority + uri.AbsolutePath).TrimEnd('/');
    }
}

/// <summary>One snapshot of a <see cref="DeviceSignInFlow"/>. <see cref="Code"/> is set while a code is live.</summary>
public sealed record DeviceSignInState(DeviceSignInStatus Status, DeviceSignInCode? Code = null)
{
    public static DeviceSignInState Idle { get; } = new(DeviceSignInStatus.Idle);

    /// <summary>True for the states a run ends in on its own: approved, denied, unavailable, failed.</summary>
    public bool IsFinal => Status is DeviceSignInStatus.Approved or DeviceSignInStatus.Denied
        or DeviceSignInStatus.Unavailable or DeviceSignInStatus.Failed;
}
