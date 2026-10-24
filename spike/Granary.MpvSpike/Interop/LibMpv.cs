using System.Runtime.InteropServices;

namespace Granary.MpvSpike.Interop;

// Slim P/Invoke layer over libmpv's client.h and render.h (API 2.x).
internal static partial class LibMpv
{
    private const string Lib = "libmpv-2.dll";

    public const int FormatNone = 0;
    public const int FormatString = 1;
    public const int FormatFlag = 3;
    public const int FormatInt64 = 4;
    public const int FormatDouble = 5;

    public const int EventNone = 0;
    public const int EventShutdown = 1;
    public const int EventLogMessage = 2;
    public const int EventStartFile = 6;
    public const int EventEndFile = 7;
    public const int EventFileLoaded = 8;
    public const int EventVideoReconfig = 17;
    public const int EventPlaybackRestart = 21;
    public const int EventPropertyChange = 22;

    public const int RenderParamInvalid = 0;
    public const int RenderParamApiType = 1;
    public const int RenderParamAdvancedControl = 10;
    public const int RenderParamD3D11InitParams = 21;
    public const int RenderParamD3D11Fbo = 22;
    public const ulong RenderUpdateFrame = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public nint Name;
        public int Format;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventLogMessage
    {
        public nint Prefix;
        public nint Level;
        public nint Text;
        public int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvRenderParam
    {
        public int Type;
        public nint Data;
    }

    [LibraryImport(Lib, EntryPoint = "mpv_client_api_version")]
    public static partial ulong ClientApiVersion();

    [LibraryImport(Lib, EntryPoint = "mpv_create")]
    public static partial nint Create();

    [LibraryImport(Lib, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(nint handle);

    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(nint handle);

    [LibraryImport(Lib, EntryPoint = "mpv_error_string")]
    public static partial nint ErrorString(int error);

    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(nint handle, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(nint handle, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetPropertyString(nint handle, string name);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetPropertyInt64(nint handle, string name, int format, out long data);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetPropertyDouble(nint handle, string name, int format, out double data);

    [LibraryImport(Lib, EntryPoint = "mpv_free")]
    public static partial void Free(nint data);

    [LibraryImport(Lib, EntryPoint = "mpv_command")]
    public static partial int Command(nint handle, nint[] args);

    [LibraryImport(Lib, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(nint handle, ulong replyUserdata, string name, int format);

    [LibraryImport(Lib, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RequestLogMessages(nint handle, string minLevel);

    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")]
    public static partial nint WaitEvent(nint handle, double timeout);

    [LibraryImport(Lib, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(nint handle);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_create")]
    public static partial int RenderContextCreate(out nint context, nint handle, MpvRenderParam[] parameters);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_set_update_callback")]
    public static unsafe partial void RenderContextSetUpdateCallback(nint context, delegate* unmanaged[Cdecl]<nint, void> callback, nint callbackContext);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_update")]
    public static partial ulong RenderContextUpdate(nint context);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_render")]
    public static partial int RenderContextRender(nint context, MpvRenderParam[] parameters);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_report_swap")]
    public static partial void RenderContextReportSwap(nint context);

    [LibraryImport(Lib, EntryPoint = "mpv_render_context_free")]
    public static partial void RenderContextFree(nint context);

    public static string Error(int code) => Marshal.PtrToStringUTF8(ErrorString(code)) ?? code.ToString();

    public static void Check(int code, string what)
    {
        if (code < 0)
        {
            throw new InvalidOperationException($"{what} failed: {Error(code)} ({code})");
        }
    }
}
