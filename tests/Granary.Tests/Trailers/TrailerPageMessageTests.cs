using Granary.Core.Trailers;

namespace Granary.Tests.Trailers;

public sealed class TrailerPageMessageTests
{
    [Theory]
    [InlineData("{\"kind\":\"ready\"}", TrailerPageEvent.Ready, 0)]
    [InlineData("{\"kind\":\"playing\"}", TrailerPageEvent.Playing, 0)]
    [InlineData("{\"kind\":\"close\"}", TrailerPageEvent.Close, 0)]
    [InlineData("{\"kind\":\"error\",\"code\":150}", TrailerPageEvent.Error, 150)]
    [InlineData(" { \"code\": 153, \"kind\": \"error\" } ", TrailerPageEvent.Error, 153)]
    public void ThePagesMessages_AreRead(string json, TrailerPageEvent kind, int code) =>
        Assert.Equal(new TrailerPageMessage(kind, code), TrailerPageMessage.Parse(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ready")]
    [InlineData("\"ready\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"Ready\"}")]
    [InlineData("{\"kind\":\"Close\"}")]
    [InlineData("{\"close\":true}")]
    [InlineData("{\"kind\":\"close\",\"code\":1}")]
    [InlineData("{\"kind\":\"navigate\",\"url\":\"https://evil.example\"}")]
    [InlineData("{\"kind\":\"ready\",\"extra\":1}")]
    [InlineData("{\"kind\":\"ready\",\"kind\":\"error\"}")]
    [InlineData("{\"kind\":\"error\",\"code\":150,\"code\":5}")]
    [InlineData("{\"kind\":\"ready\",\"code\":1}")]
    [InlineData("{\"kind\":\"error\"}")]
    [InlineData("{\"kind\":\"error\",\"code\":\"150\"}")]
    [InlineData("{\"kind\":\"error\",\"code\":1.5}")]
    [InlineData("{\"kind\":\"error\",\"code\":-1}")]
    [InlineData("{\"kind\":\"error\",\"code\":99999999999}")]
    [InlineData("{\"kind\":{\"nested\":\"ready\"}}")]
    [InlineData("{\"kind\":[\"ready\"]}")]
    [InlineData("{\"kind\":\"ready\"")]
    [InlineData("{\"kind\":\"ready\"} {}")]
    [InlineData("{\"kind\":\"ready\"} // done")]
    [InlineData("{'kind':'ready'}")]
    public void AnythingElse_IsIgnored(string? json) => Assert.Null(TrailerPageMessage.Parse(json));

    [Fact]
    public void AnOversizedMessage_IsIgnoredUnread() =>
        Assert.Null(TrailerPageMessage.Parse("{\"kind\":\"ready\"" + new string(' ', TrailerPageMessage.MaximumLength) + "}"));
}
