namespace Granary.Core.Api;

/// <summary>One field-level validation error of a problem (docs/api-contract.md, 2.2).</summary>
public sealed record ProblemError(string? Location, string? Code, string? Detail);

/// <summary>
/// RFC 9457 Problem Details as Silo's v2 API returns them. The machine-readable code is the last
/// path segment of <see cref="Type"/>; <see cref="Detail"/> is for people and never for control flow.
/// </summary>
public sealed record ApiProblem(
    string? Type,
    string? Title,
    int Status,
    string? Detail,
    string? Instance,
    IReadOnlyList<ProblemError>? Errors)
{
    public const string TypeOrigin = "https://siloserver.org/docs/api/v2/problems/";

    /// <summary>The problem code, for example <c>invalid_token</c>; <c>unknown</c> when the type is missing.</summary>
    public string Code => CodeFromType(Type);

    /// <summary>A stand-in for responses that carry no Problem Details body.</summary>
    public static ApiProblem FromStatus(int status) => new(null, null, status, null, null, null);

    internal static string CodeFromType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return ProblemCodes.Unknown;
        }
        string path = Uri.TryCreate(type, UriKind.Absolute, out var uri) ? uri.AbsolutePath : type;
        string trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string code = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        // Codes are logged; anything but a plain identifier could forge log lines.
        return code.Length is > 0 and <= 64 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            ? code
            : ProblemCodes.Unknown;
    }
}
