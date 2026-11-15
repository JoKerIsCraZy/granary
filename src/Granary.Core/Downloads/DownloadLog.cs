using System.Globalization;
using System.Text.Json;

namespace Granary.Core.Downloads;

/// <summary>
/// A failure as the downloads log it. A file system or network exception's message names the full path, which holds the
/// Windows user name, or the server's address: only its kind and code are logged, and a JSON error's place in the file.
/// </summary>
internal static class DownloadLog
{
    public static string Failure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex is JsonException json
            ? string.Create(CultureInfo.InvariantCulture, $"{nameof(JsonException)} at {json.Path ?? "$"}, line {json.LineNumber}")
            : string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
    }
}
