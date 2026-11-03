using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>
/// Copies of mpv.conf kept under a name (Settings → Playback → This PC): plain files in the mpv folder's <c>presets</c>
/// folder, <c>&lt;name&gt;.conf</c>, which the viewer can see and back up. A name is a safe file name or nothing is done.
/// Loading one, and going back from it, is in MpvPresets.Active.cs.
/// </summary>
public sealed partial class MpvPresets
{
    public const string FolderName = "presets";
    public const string Extension = ".conf";
    public const int MaxNameLength = 60;

    private static readonly char[] Refused = Path.GetInvalidFileNameChars();

    private readonly MpvConfigFolder _folder;
    private readonly ILogger<MpvPresets> _logger;

    public MpvPresets(MpvConfigFolder folder, ILogger<MpvPresets> logger)
    {
        _folder = folder;
        _logger = logger;
    }

    public string Directory => Path.Combine(_folder.Directory, FolderName);

    /// <summary>
    /// What a typed name keeps as a file name: no path separators or other characters Windows refuses, no spaces or dots
    /// at either end, at most <see cref="MaxNameLength"/> characters. Empty when nothing is left.
    /// </summary>
    public static string Clean(string? typed)
    {
        string kept = string.Concat((typed ?? "").Where(c => !Refused.Contains(c))).Trim(' ', '.');
        return kept.Length > MaxNameLength ? kept[..MaxNameLength].TrimEnd(' ', '.') : kept;
    }

    /// <summary>The typed name as a preset's name (<see cref="Clean"/>); null when nothing is left or Windows keeps the name for a device.</summary>
    public static string? SafeName(string? typed) => Clean(typed) is { Length: > 0 } name && !IsDeviceName(name) ? name : null;

    /// <summary>CON, NUL, COM1 and the like, with any extension: Windows opens the device instead of a file.</summary>
    public static bool IsDeviceName(string name) => DeviceName().IsMatch(name.Split('.')[0].TrimEnd(' '));

    /// <summary>The presets by name, ignoring case; none when the folder is missing or cannot be read.</summary>
    public IReadOnlyList<string> Names()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }
            // The backup and the markers start with a dot: Granary's own, not presets.
            return [.. System.IO.Directory.EnumerateFiles(Directory)
                .Where(file => string.Equals(Path.GetExtension(file), Extension, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(name => !name.StartsWith('.'))
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The mpv presets could not be listed");
            return [];
        }
    }

    public bool Exists(string name) => PathOf(name) is { } path && File.Exists(path);

    /// <summary>Keeps <paramref name="text"/> as the preset, replacing one of that name; false when it could not.</summary>
    public bool Save(string name, string text)
    {
        if (SafeName(name) != name || PathOf(name) is not { } path)
        {
            return false;
        }
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The mpv presets folder could not be made");
            return false;
        }
        return _folder.WriteWhole(path, text);
    }

    /// <summary>The preset's text; null when there is none of that name or it cannot be read.</summary>
    public string? Read(string name)
    {
        if (PathOf(name) is not { } path)
        {
            return null;
        }
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "An mpv preset could not be read");
            return null;
        }
    }

    public bool Delete(string name)
    {
        if (PathOf(name) is not { } path)
        {
            return false;
        }
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "An mpv preset could not be deleted");
            return false;
        }
    }

    // A preset's file, only for a name that is one file name in the folder: a name from the folder's own listing may be
    // longer than a new one could be, but never holds a separator, nor starts with the dot of Granary's own files.
    private string? PathOf(string name) =>
        name.Length > 0 && name.IndexOfAny(Refused) < 0 && name == name.Trim() && !name.StartsWith('.') && !IsDeviceName(name)
            ? Path.Combine(Directory, name + Extension)
            : null;

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();
}
