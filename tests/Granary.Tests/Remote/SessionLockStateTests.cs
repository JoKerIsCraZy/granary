using Granary.App.Services;

namespace Granary.Tests.Remote;

/// <summary>
/// Whether Windows is locked, for the phone remote (design 5.10): from a read of the session when Granary begins to follow
/// it, then from the session's notices. The read and the notices are given by the test; nothing asks Windows.
/// </summary>
public sealed class SessionLockStateTests
{
    private bool? _read;
    private int _reads;

    private SessionLockState Following()
    {
        var state = new SessionLockState(Read);
        state.Followed();
        return state;
    }

    private bool? Read()
    {
        _reads++;
        return _read;
    }

    [Fact]
    public void APcLockedAsGranaryStarts_IsLocked_BeforeAnyNotice()
    {
        // Granary started with Windows while the PC is still locked: no lock notice comes for it.
        _read = true;

        Assert.True(Following().IsLocked);
    }

    [Fact]
    public void APcUnlockedAsGranaryStarts_IsNot()
    {
        _read = false;

        Assert.False(Following().IsLocked);
    }

    [Fact]
    public void LockAndUnlock_AreFollowed_WithoutReadingAgain()
    {
        _read = false;
        var state = Following();

        state.Changed(SessionLock.Lock);
        Assert.True(state.IsLocked);
        state.Changed(SessionLock.Unlock);
        Assert.False(state.IsLocked);
        Assert.Equal(1, _reads);
    }

    [Theory]
    [InlineData(SessionLock.ConsoleDisconnect)]
    [InlineData(SessionLock.RemoteDisconnect)]
    public void ADisconnectedSession_CountsAsLocked(int reason)
    {
        // Nobody sits at a disconnected session: whoever reconnects has to sign in again.
        _read = false;
        var state = Following();

        state.Changed(reason);

        Assert.True(state.IsLocked);
    }

    [Theory]
    [InlineData(SessionLock.ConsoleConnect)]
    [InlineData(SessionLock.RemoteConnect)]
    public void AConnectedSession_IsReadAgain(int reason)
    {
        _read = false;
        var state = Following();
        state.Changed(SessionLock.ConsoleDisconnect);

        // Connected again, the session may still be locked or not: Windows is asked.
        _read = true;
        state.Changed(reason);
        Assert.True(state.IsLocked);
        _read = false;
        state.Changed(reason);
        Assert.False(state.IsLocked);
    }

    [Theory]
    [InlineData(SessionLock.Active, SessionLock.FlagLocked, true)]
    [InlineData(SessionLock.Active, SessionLock.FlagUnlocked, false)]
    [InlineData(SessionLock.Active, -1, null)]
    [InlineData(SessionLock.Disconnected, SessionLock.FlagUnlocked, true)]
    [InlineData(SessionLock.Connected, SessionLock.FlagUnlocked, false)]
    public void TheSessionsInfo_SaysWhetherItIsLocked(int connectState, int flags, bool? locked) =>
        Assert.Equal(locked, SessionLock.FromSessionInfo(connectState, flags));
}
