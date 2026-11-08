using System.Runtime.InteropServices;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// <see cref="IKeepAwake"/> through <c>SetThreadExecutionState</c>, the call mpv's own window makes for
/// <c>stop-screensaver</c> (w32_common.c); in composition mode mpv has no window, so nothing asked before. It covers the
/// display and the system alone, which <c>DisplayRequest</c> cannot, and needs no package identity. The request belongs to
/// the thread that made it and ends with it, so it is made on the UI thread only, as the player does. It shows in
/// <c>powercfg /requests</c> under DISPLAY and SYSTEM; the window's close lets it go, and so does the end of the process.
/// </summary>
internal sealed partial class WindowsKeepAwake : IKeepAwake
{
    private readonly ILogger<WindowsKeepAwake> _logger;
    private KeepAwakeState _state;

    public WindowsKeepAwake(MainWindow window, ILogger<WindowsKeepAwake> logger)
    {
        _logger = logger;
        window.Closed += (_, _) => Set(KeepAwakeState.None);
    }

    public bool Set(KeepAwakeState state)
    {
        if (state == _state)
        {
            return true;
        }
        // A call that works returns the state before, which holds ES_CONTINUOUS even on a thread's first call; 0 is a
        // refusal, which sets the error (87 for flags Windows does not take).
        if (SetThreadExecutionState(ExecutionStateFlags.For(state)) == 0)
        {
            _logger.LogWarning("Windows refused to keep awake: {State} (error {Error})", state, Marshal.GetLastPInvokeError());
            return false;
        }
        _state = state;
        switch (state)
        {
            case KeepAwakeState.Display:
                _logger.LogInformation("Keeping the display on while a title plays");
                break;
            case KeepAwakeState.System:
                _logger.LogInformation("Keeping the system awake while sound plays; the display may turn off");
                break;
            default:
                _logger.LogInformation("The display and the system may sleep again");
                break;
        }
        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint flags);
}
