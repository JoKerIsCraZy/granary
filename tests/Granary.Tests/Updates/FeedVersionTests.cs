using Granary.Core.Updates;

namespace Granary.Tests.Updates;

/// <summary>The version the unsigned update feed names, taken only when it is a plain one.</summary>
public sealed class FeedVersionTests
{
    [Theory]
    [InlineData("0.3.0")]
    [InlineData("10.20.30")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.2.3-rc-2")]
    [InlineData("1.0.0-abcdefghijklmnopqrstuvwxyz012345")]
    public void APlainVersion_IsTaken(string version)
    {
        Assert.True(FeedVersion.IsPlain(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+build")]
    [InlineData("1.0.0 ")]
    [InlineData("1.0.0\n")]
    [InlineData("1.0.0/../x?y#")]
    [InlineData("1.0.0-a/b")]
    [InlineData("1.0.0-abcdefghijklmnopqrstuvwxyz0123456")]
    [InlineData("١.٠.٠")]
    public void AnythingElse_IsNot(string? version)
    {
        Assert.False(FeedVersion.IsPlain(version));
    }
}
