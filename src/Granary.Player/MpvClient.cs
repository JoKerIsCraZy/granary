using System.Runtime.InteropServices;
using Granary.Player.Interop;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

/// <summary>
/// One libmpv instance: options, commands, properties, and an event thread. Events are raised on
/// that thread. libmpv's API itself is thread-safe. Commands and properties are only passed on while mpv runs: before
/// <see cref="Initialize"/> and from <see cref="Dispose"/> on, they do nothing and read nothing, so a caller that still
/// holds an instance the engine replaced cannot reach a handle that is gone.
/// </summary>
internal sealed class MpvClient : IDisposable
{
    private readonly ILogger _logger;
    private readonly Thread _eventThread;
    private readonly Dictionary<ulong, string> _observed = [];

    // Calls in flight hold it to read; the handle is destroyed under it to write, once they are done. Never disposed:
    // a late caller still takes it, and finds the instance closed.
    private readonly ReaderWriterLockSlim _life = new();
    private volatile bool _running;
    private volatile bool _ready;
    private nint _handle;

    public MpvClient(ILogger logger)
    {
        _logger = logger;
        _handle = LibMpv.Create();
        if (_handle == 0)
        {
            throw new InvalidOperationException("mpv_create failed.");
        }
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
    }

    public event Action<string, object?>? PropertyChanged;

    public event Action<string, string, string>? LogMessage;

    /// <summary>End of a file: mpv end reason and error code.</summary>
    public event Action<int, int>? FileEnded;

    public event Action<int>? LifecycleEvent;

    public void SetOption(string name, string value)
    {
        int rc = LibMpv.SetOptionString(_handle, name, value);
        if (rc < 0)
        {
            throw new InvalidOperationException($"mpv option '{name}' was rejected: {LibMpv.Error(rc)}");
        }
    }

    /// <summary>An option mpv may not know, such as one a newer mpv renamed; mpv's error code, below 0 when it refused.</summary>
    public int TrySetOption(string name, string value) => LibMpv.SetOptionString(_handle, name, value);

    /// <summary>
    /// Reads options and profiles in mpv's config syntax, given as text (mpv's <c>memory://</c>), as if they were set one by
    /// one with <see cref="SetOption"/>; only before <see cref="Initialize"/>.
    /// </summary>
    public void LoadConfigText(string text)
    {
        int rc = LibMpv.LoadConfigFile(_handle, "memory://" + text);
        if (rc < 0)
        {
            throw new InvalidOperationException($"mpv did not read the config text: {LibMpv.Error(rc)}");
        }
    }

    /// <summary>
    /// Starts mpv. With <paramref name="logFromStart"/>, mpv's log is asked for before, so what mpv says about the config
    /// files it reads in <c>mpv_initialize</c> reaches the log too; otherwise it is asked for after, as it always was.
    /// </summary>
    public void Initialize(IEnumerable<(string Name, int Format)> observedProperties, bool logFromStart = false)
    {
        if (logFromStart)
        {
            LibMpv.RequestLogMessages(_handle, "info");
        }
        int rc = LibMpv.Initialize(_handle);
        if (rc < 0)
        {
            // The event thread never runs for a start that failed: mpv's own words on why are passed on here.
            DispatchPendingLog();
            throw new InvalidOperationException($"mpv_initialize failed: {LibMpv.Error(rc)}");
        }
        if (!logFromStart)
        {
            LibMpv.RequestLogMessages(_handle, "info");
        }
        foreach (var (name, format) in observedProperties)
        {
            ulong id = (ulong)(_observed.Count + 1);
            _observed[id] = name;
            LibMpv.ObserveProperty(_handle, id, name, format);
        }
        _running = true;
        _ready = true;
        _eventThread.Start();
    }

    // A call on the running mpv, or what a closed one gives.
    private T Live<T>(Func<nint, T> call, T closed)
    {
        _life.EnterReadLock();
        try
        {
            return _ready && _handle != 0 ? call(_handle) : closed;
        }
        finally
        {
            _life.ExitReadLock();
        }
    }

    public int Command(params string[] args)
    {
        var pointers = new nint[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }
            int rc = Live(handle => LibMpv.Command(handle, pointers), LibMpv.ErrorUninitialized);
            if (rc < 0)
            {
                _logger.LogWarning("mpv command {Command} failed: {Error}", args[0], LibMpv.Error(rc));
            }
            return rc;
        }
        finally
        {
            foreach (nint pointer in pointers)
            {
                if (pointer != 0)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    public int SetProperty(string name, string value) =>
        Live(handle => LibMpv.SetPropertyString(handle, name, value), LibMpv.ErrorUninitialized);

    public string? GetString(string name) => Live(handle =>
    {
        nint value = LibMpv.GetPropertyString(handle, name);
        if (value == 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            LibMpv.Free(value);
        }
    }, null);

    public long? GetInt64(string name) =>
        Live(handle => LibMpv.GetPropertyInt64(handle, name, LibMpv.FormatInt64, out long value) >= 0 ? value : (long?)null, null);

    public double? GetDouble(string name) =>
        Live(handle => LibMpv.GetPropertyDouble(handle, name, LibMpv.FormatDouble, out double value) >= 0 ? value : (double?)null, null);

    private void EventLoop()
    {
        while (_running)
        {
            var ev = Marshal.PtrToStructure<LibMpv.MpvEvent>(LibMpv.WaitEvent(_handle, 0.25));
            try
            {
                Dispatch(ev);
            }
            catch (Exception ex)
            {
                // A faulty subscriber must not stop the event loop.
                _logger.LogError(ex, "mpv event handler failed for event {EventId}", ev.EventId);
            }
        }
    }

    private void DispatchPendingLog()
    {
        while (Marshal.PtrToStructure<LibMpv.MpvEvent>(LibMpv.WaitEvent(_handle, 0)) is { EventId: not LibMpv.EventNone } ev)
        {
            if (ev.EventId != LibMpv.EventLogMessage)
            {
                continue;
            }
            try
            {
                Dispatch(ev);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "mpv log handler failed");
            }
        }
    }

    private void Dispatch(LibMpv.MpvEvent ev)
    {
        switch (ev.EventId)
        {
            case LibMpv.EventNone:
                return;
            case LibMpv.EventShutdown:
                _running = false;
                return;
            case LibMpv.EventLogMessage:
                var log = Marshal.PtrToStructure<LibMpv.MpvEventLogMessage>(ev.Data);
                LogMessage?.Invoke(
                    Marshal.PtrToStringUTF8(log.Prefix) ?? "",
                    Marshal.PtrToStringUTF8(log.Level) ?? "",
                    (Marshal.PtrToStringUTF8(log.Text) ?? "").TrimEnd());
                return;
            case LibMpv.EventPropertyChange:
                var property = Marshal.PtrToStructure<LibMpv.MpvEventProperty>(ev.Data);
                PropertyChanged?.Invoke(Marshal.PtrToStringUTF8(property.Name) ?? "", ReadValue(property));
                return;
            case LibMpv.EventEndFile:
                var end = Marshal.PtrToStructure<LibMpv.MpvEventEndFile>(ev.Data);
                FileEnded?.Invoke(end.Reason, end.Error);
                return;
            default:
                LifecycleEvent?.Invoke(ev.EventId);
                return;
        }
    }

    private static object? ReadValue(LibMpv.MpvEventProperty property)
    {
        if (property.Data == 0)
        {
            return null;
        }
        return property.Format switch
        {
            LibMpv.FormatDouble => Marshal.PtrToStructure<double>(property.Data),
            LibMpv.FormatFlag => Marshal.ReadInt32(property.Data) != 0,
            LibMpv.FormatInt64 => Marshal.ReadInt64(property.Data),
            LibMpv.FormatString => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data)),
            _ => null,
        };
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }
        _running = false;
        _ready = false;
        LibMpv.Wakeup(_handle);
        if (_eventThread.IsAlive && _eventThread != Thread.CurrentThread)
        {
            _eventThread.Join(TimeSpan.FromSeconds(2));
        }
        _life.EnterWriteLock();
        try
        {
            LibMpv.TerminateDestroy(_handle);
            _handle = 0;
        }
        finally
        {
            _life.ExitWriteLock();
        }
    }
}
