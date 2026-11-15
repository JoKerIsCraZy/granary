using System.Runtime.InteropServices;
using Granary.App.Interop;
using Granary.Core.Remote;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// Whether Windows is locked, for the phone remote: a phone starts no title on a locked PC (design 5.10). It asks Windows
/// for the session's notices for the main window (<c>WTSRegisterSessionNotification</c>) and follows their lock and unlock
/// through the window's message hook (<see cref="WindowMessageHook"/>); unlocked until Windows says otherwise. It reads no
/// other state of the session and logs only that the PC locked or unlocked.
/// </summary>
internal sealed partial class WindowsPcLock(ILogger<WindowsPcLock> logger) : IPcLock, IDisposable
{
    // NOTIFY_FOR_THIS_SESSION: this session's notices only.
    private const uint ThisSession = 0;

    private nint _hwnd;
    private bool _isLocked;

    public bool IsLocked => Volatile.Read(ref _isLocked);

    /// <summary>Follows the session's lock for <paramref name="hwnd"/>, the main window, whose messages the hook sees. Once.</summary>
    public void Follow(nint hwnd)
    {
        if (_hwnd != 0)
        {
            return;
        }
        WindowMessageHook.SessionLockChanged += OnLockChanged;
        if (!WTSRegisterSessionNotification(hwnd, ThisSession))
        {
            WindowMessageHook.SessionLockChanged -= OnLockChanged;
            logger.LogWarning("The PC's lock cannot be followed (error {Error}); a phone may start titles while it is locked",
                Marshal.GetLastPInvokeError());
            return;
        }
        _hwnd = hwnd;
    }

    /// <summary>The window closes: Windows sends it no more of the session's notices.</summary>
    public void Dispose()
    {
        if (_hwnd == 0)
        {
            return;
        }
        WindowMessageHook.SessionLockChanged -= OnLockChanged;
        _ = WTSUnRegisterSessionNotification(_hwnd);
        _hwnd = 0;
    }

    // Inside the window's message, on the UI thread: nothing may escape (the hook catches it anyway).
    private void OnLockChanged(bool locked)
    {
        Volatile.Write(ref _isLocked, locked);
        if (locked)
        {
            logger.LogInformation("The PC locked");
        }
        else
        {
            logger.LogInformation("The PC unlocked");
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint hwnd, uint flags);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(nint hwnd);
}
