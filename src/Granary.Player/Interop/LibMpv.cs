using System.Runtime.InteropServices;

namespace Granary.Player.Interop;

/// <summary>Slim P/Invoke layer over the parts of libmpv's client.h (API 2.x) that Granary uses.</summary>
internal static partial class LibMpv
{
    public const string LibraryName = "libmpv-2.dll";

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

    public const int EndFileEof = 0;
    public const int EndFileStop = 2;
    public const int EndFileQuit = 3;
    public const int EndFileError = 4;
    public const int EndFileRedirect = 5;

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

    [LibraryImport(LibraryName, EntryPoint = "mpv_client_api_version")]
    public static partial ulong ClientApiVersion();

    [LibraryImport(LibraryName, EntryPoint = "mpv_create")]
    public static partial nint Create();

    [LibraryImport(LibraryName, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "mpv_error_string")]
    public static partial nint ErrorString(int error);

    [LibraryImport(LibraryName, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(nint handle, string name, string data);

    [LibraryImport(LibraryName, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(nint handle, string name, string data);

    [LibraryImport(LibraryName, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetPropertyString(nint handle, string name);

    [LibraryImport(LibraryName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetPropertyInt64(nint handle, string name, int format, out long data);

    [LibraryImport(LibraryName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetPropertyDouble(nint handle, string name, int format, out double data);

    [LibraryImport(LibraryName, EntryPoint = "mpv_free")]
    public static partial void Free(nint data);

    [LibraryImport(LibraryName, EntryPoint = "mpv_command")]
    public static partial int Command(nint handle, nint[] args);

    [LibraryImport(LibraryName, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(nint handle, ulong replyUserdata, string name, int format);

    [LibraryImport(LibraryName, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RequestLogMessages(nint handle, string minLevel);

    [LibraryImport(LibraryName, EntryPoint = "mpv_wait_event")]
    public static partial nint WaitEvent(nint handle, double timeout);

    [LibraryImport(LibraryName, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(nint handle);

    public static string Error(int code) => Marshal.PtrToStringUTF8(ErrorString(code)) ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
