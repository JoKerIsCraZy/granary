using System.Runtime.InteropServices;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// <see cref="IKeepAwake"/> through Windows' power requests (<c>PowerCreateRequest</c>, <c>PowerSetRequest</c>), so
/// <c>powercfg /requests</c> shows Granary.exe with a reason: "Playing video" under DISPLAY and SYSTEM, or "Playing audio"
/// under EXECUTION and SYSTEM (<see cref="KeepAwakeRequests"/>). mpv's own <c>stop-screensaver</c> asks only from a window
/// of its own (w32_common.c), which composition mode has not, so nothing asked before.
/// </summary>
/// <remarks>
/// A request belongs to its object, not to a thread; the player asks on the UI thread, and a lock keeps the state whole if
/// another thread asks too. Without request objects (<c>PowerCreateRequest</c> failed) it falls back on
/// <c>SetThreadExecutionState</c>, whose request belongs to the calling thread and ends with it. The window's close, or
/// <see cref="Dispose"/>, clears everything and closes the objects; the end of the process does too.
/// </remarks>
internal sealed partial class WindowsKeepAwake : IKeepAwake, IDisposable
{
    // REASON_CONTEXT's version (POWER_REQUEST_CONTEXT_VERSION) and its flag for a plain reason.
    private const uint ContextVersion = 0;
    private const uint ContextSimpleString = 0x1;
    private const nint InvalidHandle = -1;

    private readonly ILogger<WindowsKeepAwake> _logger;
    private readonly Lock _gate = new();
    // One request object per reason; null when Windows made none, and the thread's execution state stands in.
    private readonly Dictionary<string, nint>? _requests;
    // What is set on the request objects now.
    private readonly HashSet<PowerRequest> _active = [];
    private KeepAwakeState _state;
    private bool _disposed;

    public WindowsKeepAwake(MainWindow window, ILogger<WindowsKeepAwake> logger)
    {
        _logger = logger;
        _requests = CreateRequests();
        window.Closed += (_, _) => Dispose();
    }

    public bool Set(KeepAwakeState state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return state == KeepAwakeState.None;
            }
            // Unchanged only when Windows holds just what the state wants: a change that stopped half-way is finished now.
            if (state == _state && (_requests is null || Holds(state)))
            {
                return true;
            }
            if (!(_requests is { } requests ? Change(requests, state) : SetThreadState(state)))
            {
                return false;
            }
            _state = state;
            LogHeld(state);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_requests is { } requests)
            {
                foreach (var request in _active)
                {
                    PowerClearRequest(requests[request.Reason], request.Type);
                }
                _active.Clear();
                foreach (nint handle in requests.Values)
                {
                    CloseHandle(handle);
                }
            }
            else if (_state != KeepAwakeState.None)
            {
                SetThreadExecutionState(ExecutionStateFlags.Continuous);
            }
            if (_state != KeepAwakeState.None)
            {
                _state = KeepAwakeState.None;
                LogHeld(_state);
            }
        }
    }

    // Both objects or none: without them, the thread's execution state stands in, which is said once.
    private Dictionary<string, nint>? CreateRequests()
    {
        var made = new Dictionary<string, nint>();
        foreach (string reason in KeepAwakeRequests.Reasons)
        {
            nint handle = Create(reason);
            if (handle is InvalidHandle or 0)
            {
                _logger.LogWarning("Windows made no power request (error {Error}); keeping awake through SetThreadExecutionState instead",
                    Marshal.GetLastPInvokeError());
                foreach (nint other in made.Values)
                {
                    CloseHandle(other);
                }
                return null;
            }
            made[reason] = handle;
        }
        return made;
    }

    // Windows copies the reason, so it is pinned only for the call.
    private static unsafe nint Create(string reason)
    {
        fixed (char* text = reason)
        {
            var context = new ReasonContext { Version = ContextVersion, Flags = ContextSimpleString, SimpleReasonString = (nint)text };
            return PowerCreateRequest(in context);
        }
    }

    private bool Holds(KeepAwakeState state) => KeepAwakeRequests.Changes(_active, state) is { Set.Count: 0, Clear.Count: 0 };

    // Under the gate. The new requests first, then the old ones cleared, so nothing lapses in between; true once Windows
    // holds just what the state wants. A set Windows refuses takes back the ones this pass set, so what was held before
    // still is; a clear it refuses is tried again by the next call.
    private bool Change(Dictionary<string, nint> requests, KeepAwakeState state)
    {
        var (set, clear) = KeepAwakeRequests.Changes(_active, state);
        var setNow = new List<PowerRequest>();
        foreach (var request in set)
        {
            if (!PowerSetRequest(requests[request.Reason], request.Type))
            {
                _logger.LogWarning("Windows refused the power request {Type} for \"{Reason}\" (error {Error})", request.Type, request.Reason,
                    Marshal.GetLastPInvokeError());
                foreach (var taken in setNow)
                {
                    if (PowerClearRequest(requests[taken.Reason], taken.Type))
                    {
                        _active.Remove(taken);
                    }
                }
                return false;
            }
            _active.Add(request);
            setNow.Add(request);
        }
        bool cleared = true;
        foreach (var request in clear)
        {
            if (PowerClearRequest(requests[request.Reason], request.Type))
            {
                _active.Remove(request);
            }
            else
            {
                _logger.LogWarning("Windows did not clear the power request {Type} for \"{Reason}\" (error {Error})", request.Type, request.Reason,
                    Marshal.GetLastPInvokeError());
                cleared = false;
            }
        }
        return cleared;
    }

    // A call that works returns the state before, which holds ES_CONTINUOUS even on a thread's first call; 0 is a refusal.
    private bool SetThreadState(KeepAwakeState state)
    {
        if (SetThreadExecutionState(ExecutionStateFlags.For(state)) != 0)
        {
            return true;
        }
        _logger.LogWarning("Windows refused to keep awake: {State} (error {Error})", state, Marshal.GetLastPInvokeError());
        return false;
    }

    private void LogHeld(KeepAwakeState state)
    {
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
    }

    // REASON_CONTEXT (minwinbase.h) with a plain reason: the version, the flags and the string, which starts a union whose
    // larger member makes the whole 32 bytes on x64.
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint PowerCreateRequest(in ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint request, PowerRequestType type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint request, PowerRequestType type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint flags);
}
