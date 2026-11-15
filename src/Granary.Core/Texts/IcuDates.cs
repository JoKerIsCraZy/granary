using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Granary.Core.Texts;

/// <summary>
/// Dates as ICU writes them, which is what Intl.DateTimeFormat does in a browser: the locale's best pattern for a
/// skeleton of fields (CLDR's availableFormats, <c>udatpg_getBestPattern</c>), in its calendar and digits. .NET has no
/// skeletons, and its standard patterns differ from Intl's (German "05.11.2026" for Intl's "5.11.2026"). Windows ships
/// ICU as icu.dll, which .NET's own globalization uses; where it cannot be loaded, the caller writes .NET's patterns.
/// </summary>
internal static partial class IcuDates
{
    private const string Library = "icu.dll";
    private const int PatternStyle = -2; // UDAT_PATTERN: the pattern given, no style
    private const int BufferOverflow = 15; // U_BUFFER_OVERFLOW_ERROR
    private const int Capacity = 128;
    private const string Utc = "UTC";

    // A pattern once found serves every later date; ICU's generator itself is not shared between threads.
    private static readonly ConcurrentDictionary<(string Locale, string Skeleton), string> Patterns = new();
    private static volatile bool _unavailable;

    /// <summary>
    /// <paramref name="wallClock"/> as it reads, whatever its kind, written in <paramref name="locale"/>'s best pattern for
    /// <paramref name="skeleton"/>; null where ICU cannot be used.
    /// </summary>
    public static string? Format(string locale, string skeleton, DateTime wallClock)
    {
        if (_unavailable)
        {
            return null;
        }
        try
        {
            string pattern = Patterns.GetOrAdd((locale, skeleton), static key => BestPattern(key.Locale, key.Skeleton) ?? "");
            return pattern.Length == 0 ? null : FormatWith(locale, pattern, wallClock);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _unavailable = true;
            return null;
        }
    }

    private static string? BestPattern(string locale, string skeleton)
    {
        int status = 0;
        nint generator = OpenGenerator(locale, ref status);
        if (generator == 0 || status > 0)
        {
            return null;
        }
        try
        {
            char[] buffer = new char[Capacity];
            int length = GetBestPattern(generator, skeleton, skeleton.Length, buffer, buffer.Length, ref status);
            return status > 0 || length > buffer.Length ? null : new string(buffer, 0, length);
        }
        finally
        {
            CloseGenerator(generator);
        }
    }

    // The wall clock read as UTC and written in UTC: the day and hour it shows stay as they are.
    private static string? FormatWith(string locale, string pattern, DateTime wallClock)
    {
        int status = 0;
        nint format = OpenFormat(PatternStyle, PatternStyle, locale, Utc, Utc.Length, pattern, pattern.Length, ref status);
        if (format == 0 || status > 0)
        {
            return null;
        }
        try
        {
            double milliseconds = (DateTime.SpecifyKind(wallClock, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds;
            char[] buffer = new char[Capacity];
            int length = FormatDate(format, milliseconds, buffer, buffer.Length, 0, ref status);
            if (status == BufferOverflow)
            {
                status = 0;
                buffer = new char[length];
                length = FormatDate(format, milliseconds, buffer, buffer.Length, 0, ref status);
            }
            return status > 0 ? null : new string(buffer, 0, length);
        }
        finally
        {
            CloseFormat(format);
        }
    }

    [LibraryImport(Library, EntryPoint = "udatpg_open")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenGenerator([MarshalAs(UnmanagedType.LPUTF8Str)] string locale, ref int status);

    [LibraryImport(Library, EntryPoint = "udatpg_getBestPattern", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetBestPattern(nint generator, string skeleton, int length, [Out] char[] pattern, int capacity, ref int status);

    [LibraryImport(Library, EntryPoint = "udatpg_close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CloseGenerator(nint generator);

    [LibraryImport(Library, EntryPoint = "udat_open", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenFormat(
        int timeStyle, int dateStyle, [MarshalAs(UnmanagedType.LPUTF8Str)] string locale, string zone, int zoneLength,
        string pattern, int patternLength, ref int status);

    [LibraryImport(Library, EntryPoint = "udat_format", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int FormatDate(nint format, double date, [Out] char[] result, int capacity, nint position, ref int status);

    [LibraryImport(Library, EntryPoint = "udat_close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CloseFormat(nint format);
}
