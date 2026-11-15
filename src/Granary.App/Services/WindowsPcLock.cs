using System.Runtime.InteropServices;
using Granary.App.Interop;
using Granary.Core.Remote;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// Whether Windows is locked, for the phone remote: a phone starts no title on a locked PC (design 5.10). It asks Windows
/// for the session's notices for the main window (<c>WTSRegisterSessionNotification</c>), reads the session's state once
/// as they begin (<c>WTSQuerySessionInformationW</c>, <c>WTSSessionInfoEx</c>), since a PC still locked as Granary starts
/// with Windows sends no notice, and then follows the notices through the window's message hook
/// (<see cref="WindowMessageHook"/>); <see cref="SessionLockState"/> decides. It fails closed: should Windows not take the
/// window for the notices, the session is read at each question, and what Windows cannot say counts as locked. It reads
/// only whether the session is locked and connected, and logs only that the PC locked or unlocked.
/// </summary>
internal sealed partial class WindowsPcLock : IPcLock, IDisposable
{
    // NOTIFY_FOR_THIS_SESSION: this session's notices only.
    private const uint ThisSession = 0;
    // WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION and WTS_INFO_CLASS's WTSSessionInfoEx.
    private const nint CurrentServer = 0;
    private const int CurrentSession = -1;
    private const int SessionInfoEx = 25;
    // WTSINFOEXW: Level (a DWORD), then WTSINFOEX_LEVEL1_W at 8, where its LARGE_INTEGERs align it: SessionId at 8,
    // SessionState at 12, SessionFlags at 16.
    private const int StateOffset = 12;
    private const int FlagsOffset = 16;

    private readonly ILogger<WindowsPcLock> _logger;
    private readonly SessionLockState _state;
    private nint _hwnd;

    public WindowsPcLock(ILogger<WindowsPcLock> logger)
    {
        _logger = logger;
        _state = new SessionLockState(ReadSession);
    }

    public bool IsLocked => _state.IsLocked;

    /// <summary>Follows the session's lock for <paramref name="hwnd"/>, the main window, whose messages the hook sees. Once.</summary>
    public void Follow(nint hwnd)
    {
        if (_hwnd != 0)
        {
            return;
        }
        WindowMessageHook.SessionChanged += OnSessionChanged;
        if (!WTSRegisterSessionNotification(hwnd, ThisSession))
        {
            WindowMessageHook.SessionChanged -= OnSessionChanged;
            _logger.LogWarning("The PC's lock cannot be followed (error {Error}); it is read at each launch from a phone",
                Marshal.GetLastPInvokeError());
            return;
        }
        _hwnd = hwnd;
        _state.Followed();
        if (_state.IsLocked)
        {
            _logger.LogInformation("The PC locked");
        }
    }

    /// <summary>The window closes: Windows sends it no more of the session's notices.</summary>
    public void Dispose()
    {
        if (_hwnd == 0)
        {
            return;
        }
        WindowMessageHook.SessionChanged -= OnSessionChanged;
        _ = WTSUnRegisterSessionNotification(_hwnd);
        _hwnd = 0;
    }

    // Inside the window's message, on the UI thread: nothing may escape (the hook catches it anyway).
    private void OnSessionChanged(int reason)
    {
        bool before = _state.IsLocked;
        _state.Changed(reason);
        if (_state.IsLocked == before)
        {
            return;
        }
        if (_state.IsLocked)
        {
            _logger.LogInformation("The PC locked");
        }
        else
        {
            _logger.LogInformation("The PC unlocked");
        }
    }

    // Whether this session is locked or disconnected now; null when Windows does not say, which counts as locked.
    private static bool? ReadSession()
    {
        nint buffer;
        uint bytes;
        try
        {
            if (!WTSQuerySessionInformationW(CurrentServer, CurrentSession, SessionInfoEx, out buffer, out bytes))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        try
        {
            return buffer != 0 && bytes >= FlagsOffset + sizeof(int) && Marshal.ReadInt32(buffer) == 1
                ? SessionLock.FromSessionInfo(Marshal.ReadInt32(buffer, StateOffset), Marshal.ReadInt32(buffer, FlagsOffset))
                : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint hwnd, uint flags);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(nint hwnd);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
