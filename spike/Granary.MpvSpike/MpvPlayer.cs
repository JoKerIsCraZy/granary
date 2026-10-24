using System.Runtime.InteropServices;
using Granary.MpvSpike.Interop;

namespace Granary.MpvSpike;

/// <summary>Minimal libmpv wrapper for the spike: options, commands, properties, event loop.</summary>
internal sealed class MpvPlayer : IDisposable
{
    private readonly Thread _eventThread;
    private volatile bool _running;
    private readonly Dictionary<ulong, string> _observed = new();

    public nint Handle { get; private set; }

    /// <summary>Raised on the event thread: property name and value (string, double, bool or null).</summary>
    public event Action<string, object?>? PropertyChanged;

    /// <summary>Raised on the event thread for lifecycle events (file loaded, playback restart, end file).</summary>
    public event Action<int, string?>? EventRaised;

    public MpvPlayer()
    {
        Handle = LibMpv.Create();
        if (Handle == 0)
        {
            throw new InvalidOperationException("mpv_create failed");
        }
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
    }

    public void SetOption(string name, string value) =>
        LibMpv.Check(LibMpv.SetOptionString(Handle, name, value), $"option {name}");

    public void Initialize()
    {
        LibMpv.Check(LibMpv.Initialize(Handle), "mpv_initialize");
        LibMpv.RequestLogMessages(Handle, "info");
        Observe("time-pos", LibMpv.FormatDouble);
        Observe("duration", LibMpv.FormatDouble);
        Observe("pause", LibMpv.FormatFlag);
        Observe("hwdec-current", LibMpv.FormatString);
        Observe("eof-reached", LibMpv.FormatFlag);
        Observe("paused-for-cache", LibMpv.FormatFlag);
        _running = true;
        _eventThread.Start();
    }

    private void Observe(string name, int format)
    {
        ulong id = (ulong)(_observed.Count + 1);
        _observed[id] = name;
        LibMpv.Check(LibMpv.ObserveProperty(Handle, id, name, format), $"observe {name}");
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
            return LibMpv.Command(Handle, pointers);
        }
        finally
        {
            foreach (nint p in pointers)
            {
                if (p != 0)
                {
                    Marshal.FreeCoTaskMem(p);
                }
            }
        }
    }

    public int SetProperty(string name, string value) => LibMpv.SetPropertyString(Handle, name, value);

    public string? GetString(string name)
    {
        nint p = LibMpv.GetPropertyString(Handle, name);
        if (p == 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUTF8(p);
        }
        finally
        {
            LibMpv.Free(p);
        }
    }

    public long? GetInt64(string name) =>
        LibMpv.GetPropertyInt64(Handle, name, LibMpv.FormatInt64, out long v) >= 0 ? v : null;

    public double? GetDouble(string name) =>
        LibMpv.GetPropertyDouble(Handle, name, LibMpv.FormatDouble, out double v) >= 0 ? v : null;

    private void EventLoop()
    {
        while (_running)
        {
            nint ptr = LibMpv.WaitEvent(Handle, 0.25);
            var ev = Marshal.PtrToStructure<LibMpv.MpvEvent>(ptr);
            switch (ev.EventId)
            {
                case LibMpv.EventNone:
                    break;
                case LibMpv.EventShutdown:
                    _running = false;
                    break;
                case LibMpv.EventLogMessage:
                    var log = Marshal.PtrToStructure<LibMpv.MpvEventLogMessage>(ev.Data);
                    string prefix = Marshal.PtrToStringUTF8(log.Prefix) ?? "";
                    string level = Marshal.PtrToStringUTF8(log.Level) ?? "";
                    string text = (Marshal.PtrToStringUTF8(log.Text) ?? "").TrimEnd();
                    SpikeLog.Write("mpv", $"{level} {prefix}: {Redact(text)}");
                    break;
                case LibMpv.EventPropertyChange:
                    var prop = Marshal.PtrToStructure<LibMpv.MpvEventProperty>(ev.Data);
                    string name = Marshal.PtrToStringUTF8(prop.Name) ?? "";
                    PropertyChanged?.Invoke(name, ReadValue(prop));
                    break;
                case LibMpv.EventEndFile:
                    var end = Marshal.PtrToStructure<LibMpv.MpvEventEndFile>(ev.Data);
                    EventRaised?.Invoke(ev.EventId, $"reason={end.Reason} error={LibMpv.Error(end.Error)}");
                    break;
                default:
                    EventRaised?.Invoke(ev.EventId, null);
                    break;
            }
        }
    }

    private static object? ReadValue(LibMpv.MpvEventProperty prop)
    {
        if (prop.Data == 0)
        {
            return null;
        }
        return prop.Format switch
        {
            LibMpv.FormatDouble => Marshal.PtrToStructure<double>(prop.Data),
            LibMpv.FormatFlag => Marshal.ReadInt32(prop.Data) != 0,
            LibMpv.FormatInt64 => Marshal.ReadInt64(prop.Data),
            LibMpv.FormatString => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(prop.Data)),
            _ => null,
        };
    }

    // Never write credentials to the log, even if a verbose mpv line echoes an option value.
    private static string Redact(string text) =>
        text.Contains("Authorization", StringComparison.OrdinalIgnoreCase) || text.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
            ? "<redacted line containing credentials>"
            : text;

    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }
        _running = false;
        LibMpv.Wakeup(Handle);
        if (_eventThread.IsAlive)
        {
            _eventThread.Join(2000);
        }
        LibMpv.TerminateDestroy(Handle);
        Handle = 0;
    }
}
