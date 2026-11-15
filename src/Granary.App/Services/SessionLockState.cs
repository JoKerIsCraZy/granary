namespace Granary.App.Services;

/// <summary>
/// Whether Windows is locked, as a read of the session and the session's notices tell it (<c>WindowsPcLock</c>): read once
/// when the notices begin, so a PC still locked as Granary starts with Windows counts as locked, then followed. A
/// disconnected session counts as locked: nobody sits at it, and whoever connects again signs in first. It fails closed:
/// without the notices (Windows did not take the window for them) every answer is read anew, and what Windows cannot say
/// counts as locked, asked again at the next question. Apart from Windows so it can be tested: the read is given.
/// </summary>
/// <param name="read">The session's lock as Windows says it now; null when it cannot tell.</param>
internal sealed class SessionLockState(Func<bool?> read)
{
    private const int Unknown = -1;
    private const int Unlocked = 0;
    private const int Locked = 1;

    private int _state = Unknown;
    private bool _followed;

    /// <summary>The PC is locked, or it cannot be told whether it is.</summary>
    public bool IsLocked
    {
        get
        {
            int state = Volatile.Read(ref _state);
            return Volatile.Read(ref _followed) && state != Unknown ? state == Locked : read() ?? true;
        }
    }

    /// <summary>The session's notices begin: its state now, which no notice will say.</summary>
    public void Followed()
    {
        Store(read());
        Volatile.Write(ref _followed, true);
    }

    /// <summary>
    /// The notices end (the window closed, while the receiver may still answer): what they said last is not trusted any
    /// more, and every answer is read anew.
    /// </summary>
    public void Unfollowed()
    {
        Volatile.Write(ref _followed, false);
        Store(null);
    }

    /// <summary>
    /// A notice of the session (WM_WTSSESSION_CHANGE's reason): a lock, an unlock, a disconnect, or a reason to read again.
    /// None counts while the notices are not followed.
    /// </summary>
    public void Changed(int reason)
    {
        if (Volatile.Read(ref _followed))
        {
            Store(SessionLock.AfterChange(reason) ?? read());
        }
    }

    private void Store(bool? locked) => Volatile.Write(ref _state, locked switch { true => Locked, false => Unlocked, null => Unknown });
}

/// <summary>The numbers Windows says a session's state with (wtsapi32.h, WinUser.h).</summary>
internal static class SessionLock
{
    // WM_WTSSESSION_CHANGE's reasons, in its wParam.
    public const int ConsoleConnect = 0x1;
    public const int ConsoleDisconnect = 0x2;
    public const int RemoteConnect = 0x3;
    public const int RemoteDisconnect = 0x4;
    public const int Lock = 0x7;
    public const int Unlock = 0x8;

    // WTS_CONNECTSTATE_CLASS.
    public const int Active = 0;
    public const int Connected = 1;
    public const int Disconnected = 4;

    // WTSINFOEX_LEVEL1_W's SessionFlags: WTS_SESSIONSTATE_LOCK and _UNLOCK (reversed only on Windows 7, which Granary does not run on).
    public const int FlagLocked = 0;
    public const int FlagUnlocked = 1;

    /// <summary>What a notice says by itself: locked, unlocked, or null when the session has to be read again.</summary>
    public static bool? AfterChange(int reason) => reason switch
    {
        Lock or ConsoleDisconnect or RemoteDisconnect => true,
        Unlock => false,
        _ => null,
    };

    /// <summary>Whether a session with this connect state and these flags is locked; null when Windows does not say.</summary>
    public static bool? FromSessionInfo(int connectState, int flags) => connectState == Disconnected
        ? true
        : flags switch
        {
            FlagLocked => true,
            FlagUnlocked => false,
            _ => null,
        };
}
