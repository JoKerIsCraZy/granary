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

    /// <summary>The <c>Retry-After</c> delay of a 429 or 503 response, when the server sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}
