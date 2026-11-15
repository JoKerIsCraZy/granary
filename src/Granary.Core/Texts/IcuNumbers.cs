using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Granary.Core.Texts;

/// <summary>
/// What .NET's number formats do not know of a locale, asked of ICU's number formatter as Intl.NumberFormat asks it:
/// CLDR's minimum grouping digits, so a locale that writes 1234 but 12 345 (Spanish, Polish, European Portuguese) is
/// told from one that writes 1,234 (Mexican Spanish). Null where ICU cannot be used.
/// </summary>
internal static partial class IcuNumbers
{
    private const string Library = "icu.dll";
    private const int Capacity = 64;

    private static readonly ConcurrentDictionary<string, bool?> FourDigitGroups = new(StringComparer.Ordinal);
    private static volatile bool _unavailable;

    /// <summary>Whether <paramref name="locale"/> groups a four-digit number (1,234); null where ICU cannot say.</summary>
    public static bool? GroupsFourDigits(string locale)
    {
        if (_unavailable)
        {
            return null;
        }
        try
        {
            return FourDigitGroups.GetOrAdd(locale, static name => Format(name, 1234) is { } text ? !text.All(char.IsDigit) : null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _unavailable = true;
            return null;
        }
    }

    // The number in the locale's default format: the empty skeleton, grouping as CLDR's locale data says (group-auto).
    private static string? Format(string locale, double value)
    {
        int status = 0;
        nint formatter = OpenFormatter("", 0, locale, ref status);
        if (formatter == 0 || status > 0)
        {
            return null;
        }
        nint result = 0;
        try
        {
            result = OpenResult(ref status);
            if (result == 0 || status > 0)
            {
                return null;
            }
            FormatDouble(formatter, value, result, ref status);
            char[] buffer = new char[Capacity];
            int length = ResultToString(result, buffer, buffer.Length, ref status);
            return status > 0 || length > buffer.Length ? null : new string(buffer, 0, length);
        }
        finally
        {
            if (result != 0)
            {
                CloseResult(result);
            }
            CloseFormatter(formatter);
        }
    }

    [LibraryImport(Library, EntryPoint = "unumf_openForSkeletonAndLocale", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenFormatter(string skeleton, int skeletonLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string locale, ref int status);

    [LibraryImport(Library, EntryPoint = "unumf_openResult")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenResult(ref int status);

    [LibraryImport(Library, EntryPoint = "unumf_formatDouble")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void FormatDouble(nint formatter, double value, nint result, ref int status);

    [LibraryImport(Library, EntryPoint = "unumf_resultToString", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ResultToString(nint result, [Out] char[] buffer, int capacity, ref int status);

    [LibraryImport(Library, EntryPoint = "unumf_closeResult")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CloseResult(nint result);

    [LibraryImport(Library, EntryPoint = "unumf_close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CloseFormatter(nint formatter);
}
