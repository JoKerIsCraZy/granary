using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Discovery;

namespace Granary.Tests.Api;

public sealed class ApiProblemTests
{
    [Theory]
    [InlineData("https://siloserver.org/docs/api/v2/problems/invalid_token", "invalid_token")]
    [InlineData("https://siloserver.org/docs/api/v2/problems/validation_failed/", "validation_failed")]
    [InlineData("validation_failed", "validation_failed")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    public void Code_IsTheLastPathSegmentOfType(string? type, string expected)
    {
        var problem = new ApiProblem(type, null, 401, null, null, null);

        Assert.Equal(expected, problem.Code);
    }

    [Fact]
    public void Deserialize_ReadsProblemDetailsAndIgnoresUnknownMembers()
    {
        const string json = """
            {
              "type": "https://siloserver.org/docs/api/v2/problems/validation_failed",
              "title": "Validation failed",
              "status": 422,
              "detail": "The request did not pass validation; see errors.",
              "instance": "urn:silo:request:000000000000000000000003",
              "errors": [{ "location": "header.x-profile-id", "code": "required", "detail": "missing" }],
              "added_in_a_later_server": true
            }
            """;

        var problem = JsonSerializer.Deserialize(json, SiloJsonContext.Default.ApiProblem);

        Assert.NotNull(problem);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);
        Assert.Equal(422, problem.Status);
        var error = Assert.Single(problem.Errors!);
        Assert.Equal("header.x-profile-id", error.Location);
        Assert.Equal("required", error.Code);
    }

    [Fact]
    public void Serialize_UsesSnakeCaseAndOmitsNullMembers()
    {
        var problem = new ApiProblem("https://siloserver.org/docs/api/v2/problems/not_found", "Not found", 404, null, null, null);
        var info = new SystemInfo(2, "abc", "dev", null);

        string problemJson = JsonSerializer.Serialize(problem, SiloJsonContext.Default.ApiProblem);
        string infoJson = JsonSerializer.Serialize(info, SiloJsonContext.Default.SystemInfo);

        Assert.DoesNotContain("errors", problemJson, StringComparison.Ordinal);
        Assert.DoesNotContain("null", problemJson, StringComparison.Ordinal);
        Assert.Contains("\"api_major\":2", infoJson, StringComparison.Ordinal);
        Assert.Contains("\"contract_digest\":\"abc\"", infoJson, StringComparison.Ordinal);
    }
}
