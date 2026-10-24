using Granary.Core.Diagnostics;
using Granary.Player;

namespace Granary.Tests.Diagnostics;

public sealed class LogRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "eyJhbGciOi")]
    [InlineData("{\"access_token\":\"abc.def.ghi\",\"expires_in\":28800}", "abc.def.ghi")]
    [InlineData("{\"refresh_token\": \"r-123\"}", "r-123")]
    [InlineData("X-Profile-Token: p-456", "p-456")]
    [InlineData("GET /api/v2/stream/1?st=signed.jwt.value", "signed.jwt.value")]
    [InlineData("GET /media?token=t-789&x=1", "t-789")]
    [InlineData("device_code=dc-0001", "dc-0001")]
    public void Redact_MasksCredentialValues(string input, string secret)
    {
        string output = LogRedactor.Redact(input);

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Contains("<redacted>", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Playback started at 12.5 s")]
    [InlineData("token_refresh_required")]
    [InlineData("expires_in=28800")]
    public void Redact_LeavesOrdinaryTextAlone(string input)
    {
        Assert.Equal(input, LogRedactor.Redact(input));
    }

    [Theory]
    [InlineData("curl", "HTTP error 401", MpvNetworkFailure.Unauthorized)]
    [InlineData("curl", "HTTP error 403", MpvNetworkFailure.Forbidden)]
    [InlineData("stream", "HTTP error 404", MpvNetworkFailure.NotFound)]
    [InlineData("curl", "HTTP error 410", MpvNetworkFailure.Gone)]
    [InlineData("curl", "HTTP error 503", MpvNetworkFailure.OtherHttpError)]
    [InlineData("curl", "transfer failed: Failed writing received data", MpvNetworkFailure.None)]
    [InlineData("cplayer", "HTTP error 401", MpvNetworkFailure.None)]
    public void MpvLogClassifier_ReadsHttpStatusFromLogLines(string prefix, string text, MpvNetworkFailure expected)
    {
        Assert.Equal(expected, MpvLogClassifier.Classify(prefix, text));
    }

    [Fact]
    public void MpvLogClassifier_DropsLinesWithAnAuthorizationHeader()
    {
        string output = MpvLogClassifier.Redact("Setting option 'http-header-fields' = 'Authorization: Bearer secret'");

        Assert.DoesNotContain("secret", output, StringComparison.Ordinal);
    }
}
