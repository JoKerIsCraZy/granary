using Granary.App.Services;

namespace Granary.Tests.Remote;

/// <summary>
/// When an advertisement Windows will not take back is given up, so the PC is advertised anew rather than never again:
/// a removal Windows refused keeps the registration for a while, so no second one of the PC's name is made beside it, but
/// not for good.
/// </summary>
public sealed class DnssdRefusalsTests
{
    // DNS_ERROR_TRY_AGAIN_LATER: a refusal that says nothing of the registration.
    private const uint TryAgainLater = 9554;
    private const uint NotFound = 1168;
    private const uint InvalidParameter = 87;

    [Fact]
    public void ARefusal_KeepsTheRegistration_AtFirst()
    {
        var refusals = new DnssdRefusals();

        Assert.False(refusals.GivesUp(TryAgainLater, now: 1_000));
        Assert.False(refusals.GivesUp(TryAgainLater, now: 2_000));
    }

    [Fact]
    public void TheThirdRefusal_GivesTheRegistrationUp()
    {
        var refusals = new DnssdRefusals();
        refusals.GivesUp(TryAgainLater, now: 1_000);
        refusals.GivesUp(TryAgainLater, now: 2_000);

        Assert.True(refusals.GivesUp(TryAgainLater, now: 3_000));
        Assert.Equal(3, DnssdRefusals.Limit);
    }

    [Fact]
    public void ARefusalHalfAMinuteAfterTheFirst_GivesTheRegistrationUp()
    {
        long giveUpAfter = (long)DnssdRefusals.GiveUpAfter.TotalMilliseconds;
        var early = new DnssdRefusals();
        var late = new DnssdRefusals();
        early.GivesUp(TryAgainLater, now: 1_000);
        late.GivesUp(TryAgainLater, now: 1_000);

        Assert.False(early.GivesUp(TryAgainLater, now: 1_000 + giveUpAfter - 1));
        Assert.True(late.GivesUp(TryAgainLater, now: 1_000 + giveUpAfter));
        Assert.Equal(TimeSpan.FromSeconds(30), DnssdRefusals.GiveUpAfter);
    }

    [Theory]
    [InlineData(NotFound)]
    [InlineData(InvalidParameter)]
    public void ARequestWindowsDoesNotKnowOrTake_IsGivenUpAtOnce(uint status)
    {
        Assert.True(new DnssdRefusals().GivesUp(status, now: 1_000));
    }
}
