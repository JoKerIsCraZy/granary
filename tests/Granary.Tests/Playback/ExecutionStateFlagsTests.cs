using Granary.Core.Abstractions;

namespace Granary.Tests.Playback;

/// <summary>The flags Granary hands SetThreadExecutionState for what the player keeps awake.</summary>
public sealed class ExecutionStateFlagsTests
{
    [Theory]
    [InlineData(KeepAwakeState.Display, 0x80000003u)]
    [InlineData(KeepAwakeState.System, 0x80000001u)]
    [InlineData(KeepAwakeState.None, 0x80000000u)]
    public void EachState_IsAContinuousRequest_TheDisplayWithTheSystem(KeepAwakeState state, uint flags) =>
        Assert.Equal(flags, ExecutionStateFlags.For(state));

    [Fact]
    public void AStateNotKnown_LetsGo() =>
        Assert.Equal(ExecutionStateFlags.Continuous, ExecutionStateFlags.For((KeepAwakeState)42));
}
