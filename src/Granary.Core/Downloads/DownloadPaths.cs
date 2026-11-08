using System.Security.Cryptography;
using System.Text;

namespace Granary.Core.Downloads;

/// <summary>
/// Where a download's files go: <c>&lt;root&gt;\&lt;server&gt;\acct-&lt;account&gt;\&lt;profile&gt;\&lt;download&gt;\</c>, every
/// part one folder name made by <see cref="Segment"/>, so no id the server sends can name another path. The folder holds
/// <see cref="Record"/>, <see cref="Manifest"/>, <see cref="MediaPart"/> and then the media file, the artwork and the subtitles.
/// </summary>
public static class DownloadPaths
{
    /// <summary>The media file while it arrives; renamed to <see cref="MediaName"/> once it is whole.</summary>
    public const string MediaPart = "media.part";

    /// <summary>The server's manifest, as it was sent.</summary>
    public const string Manifest = "manifest.json";

    /// <summary>The download's own record (<see cref="DownloadRecord"/>), so its folder describes itself.</summary>
    public const string Record = "download.json";

    private const int MaxPlainLength = 40;
    private const int HashDigits = 16;
    private const int MaxExtensionLength = 8;
    private const string MediaPrefix = "media.";

    // Names Windows keeps for devices, with or without an extension.
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // The scanner's names that are not the files' usual extensions.
    private static readonly Dictionary<string, string> ContainerExtensions = new(StringComparer.Ordinal)
    {
        ["matroska"] = "mkv",
        ["mpegts"] = "ts",
    };

    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpg",
        ["image/jpg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
        ["image/avif"] = "avif",
        ["image/gif"] = "gif",
    };

    /// <summary><c>downloads</c> in the data folder: <c>%LOCALAPPDATA%\Granary\downloads</c>, or under <c>GRANARY_DATA_DIR</c>.</summary>
    public static string DefaultRoot => Path.Combine(AppPaths.DataDirectory, "downloads");

    /// <summary>The folder of download <paramref name="downloadId"/> of <paramref name="scope"/> under <paramref name="root"/>.</summary>
    public static string Folder(string root, DownloadScope scope, string downloadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(scope);
        return Path.Combine(Path.GetFullPath(root), Segment(scope.ServerEntryId), "acct-" + Segment(scope.AccountId), Segment(scope.ProfileId),
            Segment(downloadId));
    }

    /// <summary>
    /// <paramref name="value"/> as one folder name: letters, digits, <c>.</c>, <c>_</c> and <c>-</c> stay, anything else becomes
    /// <c>_</c>. A name that changed, ran over 40 characters, has a capital letter, is a device name, ends in a dot or ends as a
    /// hashed name does is cut to 40 characters and gets <c>-</c> and the first 16 hex digits of the value's SHA-256. So two
    /// ids never share a folder, also where Windows ignores case, and none is a path.
    /// </summary>
    public static string Segment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var plain = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            plain.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }
        string name = plain.ToString();
        bool device = DeviceNames.Contains(name.Split('.')[0]);
        if (name == value && name.Length <= MaxPlainLength && !device && !name.EndsWith('.') && !name.Any(char.IsAsciiLetterUpper)
            && !LooksHashed(name))
        {
            return name;
        }
        // A device name stays one with any extension: it is set apart in front as well.
        string kept = device ? "_" + name : name;
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..HashDigits];
        return kept[..Math.Min(kept.Length, MaxPlainLength)] + "-" + hash;
    }

    /// <summary>
    /// <paramref name="name"/> is a folder name <see cref="Segment"/> can make: a plain one it keeps as it is, or a hashed one. A
    /// look for download folders goes only into such names.
    /// </summary>
    public static bool IsSegment(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            return false;
        }
        return LooksHashed(name) ? name.Length - HashDigits - 1 is >= 1 and <= MaxPlainLength : Segment(name) == name;
    }

    /// <summary>The finished media file's name, after the delivered file's container: <c>media.mkv</c>; <c>media.bin</c> when unknown.</summary>
    public static string MediaName(string? container)
    {
        string? first = container?.Split(',')[0].Trim().ToLowerInvariant();
        string? extension = first is not null && ContainerExtensions.TryGetValue(first, out var known) ? known : first;
        string name = MediaPrefix + Extension(extension, "bin");
        return name == MediaPart ? MediaPrefix + "bin" : name;
    }

    /// <summary>
    /// <paramref name="name"/> is a finished media file's name as <see cref="MediaName"/> makes them: <c>media.</c> and a short
    /// lower-case extension, never the part, the record or the manifest.
    /// </summary>
    public static bool IsMediaName(string? name) =>
        name is not null && name.StartsWith(MediaPrefix, StringComparison.Ordinal) && name != MediaPart
        && name[MediaPrefix.Length..] is { Length: > 0 and <= MaxExtensionLength } extension
        && extension.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

    // A plain name that ends as Segment's hashed ones do, "-" and 16 lower-case hex digits: left as it is, it could be another
    // id's folder.
    private static bool LooksHashed(string name) =>
        name.Length > HashDigits && name[^(HashDigits + 1)] == '-' && name[^HashDigits..].All(char.IsAsciiHexDigitLower);

    /// <summary>An artwork file, <c>poster.jpg</c>: its kind and the extension of its media type (<c>img</c> when unknown).</summary>
    public static string ArtworkName(string kind, string? mediaType) =>
        Segment(kind) + "." + (mediaType is not null && ImageExtensions.TryGetValue(mediaType.Trim(), out var extension) ? extension : "img");

    /// <summary><paramref name="name"/> is a name <see cref="ArtworkName"/> makes for <paramref name="kind"/>, whatever its media type.</summary>
    public static bool IsArtworkName(string? kind, string? name) =>
        !string.IsNullOrEmpty(kind) && name is not null
        && ImageExtensions.Values.Append("img").Any(extension => name == Segment(kind) + "." + extension);

    /// <summary>The manifest's subtitle <paramref name="index"/>, <c>sub-0.srt</c>, after its format (<c>sub</c> when unknown).</summary>
    public static string SubtitleName(int index, string? format)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return $"sub-{index}.{Extension(format?.Trim().ToLowerInvariant(), "sub")}";
    }

    // A plain short extension, or the fallback: nothing from the server reaches a file name unchecked.
    private static string Extension(string? value, string fallback) =>
        value is { Length: > 0 and <= MaxExtensionLength } && value.All(char.IsAsciiLetterOrDigit) ? value.ToLowerInvariant() : fallback;
}
