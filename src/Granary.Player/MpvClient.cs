using System.Runtime.InteropServices;
using Granary.Player.Interop;
using Microsoft.Extensions.Logging;

namespace Granary.Player;

/// <summary>
/// One libmpv instance: options, commands, properties, and an event thread. Events are raised on
/// that thread. libmpv's API itself is thread-safe.
/// </summary>
internal sealed class MpvClient : IDisposable
{
    private readonly ILogger _logger;
    private readonly Thread _eventThread;
    private readonly Dictionary<ulong, string> _observed = [];
    private volatile bool _running;
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

    public void Initialize(IEnumerable<(string Name, int Format)> observedProperties)
    {
        int rc = LibMpv.Initialize(_handle);
        if (rc < 0)
        {
            throw new InvalidOperationException($"mpv_initialize failed: {LibMpv.Error(rc)}");
        }
        LibMpv.RequestLogMessages(_handle, "info");
        foreach (var (name, format) in observedProperties)
        {
            ulong id = (ulong)(_observed.Count + 1);
            _observed[id] = name;
            LibMpv.ObserveProperty(_handle, id, name, format);
        }
        _running = true;
        _eventThread.Start();
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
            int rc = LibMpv.Command(_handle, pointers);
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

    public int SetProperty(string name, string value) => LibMpv.SetPropertyString(_handle, name, value);

    public string? GetString(string name)
    {
        nint value = LibMpv.GetPropertyString(_handle, name);
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
    }

    public long? GetInt64(string name) =>
        LibMpv.GetPropertyInt64(_handle, name, LibMpv.FormatInt64, out long value) >= 0 ? value : null;

    public double? GetDouble(string name) =>
        LibMpv.GetPropertyDouble(_handle, name, LibMpv.FormatDouble, out double value) >= 0 ? value : null;

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
        LibMpv.Wakeup(_handle);
        if (_eventThread.IsAlive)
        {
            _eventThread.Join(TimeSpan.FromSeconds(2));
        }
        LibMpv.TerminateDestroy(_handle);
        _handle = 0;
    }
}
