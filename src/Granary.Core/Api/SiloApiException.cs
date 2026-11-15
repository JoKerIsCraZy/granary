namespace Granary.Core.Api;

/// <summary>A non-success v2 response. The message holds only status and problem code, never secrets.</summary>
public sealed class SiloApiException : Exception
{
    public SiloApiException(int status, ApiProblem problem, TimeSpan? retryAfter = null)
        : base($"Silo API returned {status} {problem.Code}")
    {
        Status = status;
        Problem = problem;
        RetryAfter = retryAfter;
    }

    public int Status { get; }

    public ApiProblem Problem { get; }

    public string Code => Problem.Code;

    /// <summary>
    /// The answer was a problem the server wrote: its type lies under Silo's own origin (<see cref="ApiProblem.TypeOrigin"/>),
    /// as every problem the server writes does (<c>internal/apiv2/problem.go</c>). Otherwise only the status is known, and that
    /// can be anyone's answer: a proxy's error page, a network's sign-in page, a filter's refusal, also one written as a
    /// problem of its own (<c>about:blank</c>).
    /// </summary>
    public bool HasProblem => Problem.Type?.StartsWith(ApiProblem.TypeOrigin, StringComparison.Ordinal) == true;

    /// <summary>The <c>Retry-After</c> delay of a 429 or 503 response, when the server sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}
