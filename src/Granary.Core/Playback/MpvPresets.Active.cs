using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// Loading a preset, and going back from it. The first load keeps the viewer's own mpv.conf as it was, byte for byte, in
// .before-preset.conf (or marks that there was none, .before-preset.none); a later load keeps that first backup, so Unload
// always returns to the file from before any preset. .active.json names the preset loaded last and the hash of its text,
// which tells whether mpv.conf was changed since. Deleting the preset's file leaves all of this as it is.
public sealed partial class MpvPresets
{
    private string BackupFile => Path.Combine(Directory, ".before-preset" + Extension);

    private string NoFileMarker => Path.Combine(Directory, ".before-preset.none");

    private string ActiveFile => Path.Combine(Directory, ".active.json");

    /// <summary>
    /// Replaces mpv.conf with the preset, after keeping the viewer's own file if no preset is loaded yet; false, with
    /// mpv.conf as it was, when the preset cannot be read or mpv.conf not written.
    /// </summary>
    public bool Load(string name)
    {
        if (Read(name) is not { } text || _folder.ConfigFileIsFolder)
        {
            return false;
        }
        bool backedUp = false;
        if (!File.Exists(BackupFile) && !File.Exists(NoFileMarker))
        {
            if (!BackUp())
            {
                return false;
            }
            backedUp = true;
        }
        if (!_folder.WriteConfig(text))
        {
            if (backedUp)
            {
                DeleteQuietly(BackupFile, NoFileMarker);
            }
            return false;
        }
        WriteActive(new ActivePresetFile(name, Hash(Encoding.UTF8.GetBytes(text))));
        return true;
    }

    /// <summary>The preset loaded last, and whether mpv.conf differs from it now; null when none is loaded.</summary>
    public MpvActivePreset? Active()
    {
        try
        {
            if (!File.Exists(ActiveFile)
                || JsonSerializer.Deserialize(File.ReadAllText(ActiveFile), MpvPresetsJsonContext.Default.ActivePresetFile) is not { Name: { } name } active)
            {
                return null;
            }
            string? now = File.Exists(_folder.ConfigFile) ? Hash(File.ReadAllBytes(_folder.ConfigFile)) : null;
            return new MpvActivePreset(name, !string.Equals(now, active.Sha256, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "The mpv preset in use could not be read");
            return null;
        }
    }

    /// <summary>
    /// Goes back to mpv.conf from before the first preset: the backup put back, or mpv.conf removed when there was none.
    /// The backup and the active preset go once that is done; a failure leaves them for another try.
    /// </summary>
    public MpvPresetUnload Unload()
    {
        MpvPresetUnload result;
        try
        {
            if (File.Exists(NoFileMarker))
            {
                File.Delete(_folder.ConfigFile);
                result = MpvPresetUnload.Removed;
            }
            else if (File.Exists(BackupFile))
            {
                result = _folder.WriteWhole(_folder.ConfigFile, File.ReadAllBytes(BackupFile)) ? MpvPresetUnload.Restored : MpvPresetUnload.Failed;
            }
            else
            {
                _logger.LogWarning("There is no mpv.conf from before the preset to go back to");
                result = MpvPresetUnload.NothingToRestore;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "mpv.conf could not go back to how it was before the preset");
            result = MpvPresetUnload.Failed;
        }
        if (result != MpvPresetUnload.Failed)
        {
            DeleteQuietly(BackupFile, NoFileMarker, ActiveFile);
        }
        return result;
    }

    // The viewer's own mpv.conf, byte for byte, or the mark that there was none.
    private bool BackUp()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            return File.Exists(_folder.ConfigFile)
                ? _folder.WriteWhole(BackupFile, File.ReadAllBytes(_folder.ConfigFile))
                : _folder.WriteWhole(NoFileMarker, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "mpv.conf could not be kept before loading a preset");
            return false;
        }
    }

    // Logged when it fails: mpv.conf has the preset either way, only the line in Settings does not name it.
    private void WriteActive(ActivePresetFile active) =>
        _folder.WriteWhole(ActiveFile, JsonSerializer.Serialize(active, MpvPresetsJsonContext.Default.ActivePresetFile));

    private void DeleteQuietly(params string[] files)
    {
        foreach (string file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "{File} could not be deleted", Path.GetFileName(file));
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>The preset loaded last; <paramref name="IsChanged"/> when mpv.conf no longer holds its text.</summary>
public sealed record MpvActivePreset(string Name, bool IsChanged);

/// <summary>How going back from a preset went.</summary>
public enum MpvPresetUnload
{
    /// <summary>The viewer's own mpv.conf is back.</summary>
    Restored,

    /// <summary>There was no mpv.conf before the preset, and now there is none again.</summary>
    Removed,

    /// <summary>The backup was gone; mpv.conf stays, and no preset is named any more.</summary>
    NothingToRestore,

    /// <summary>mpv.conf could not be put back; the backup stays.</summary>
    Failed,
}

/// <summary><c>presets\.active.json</c>: the preset's name and the SHA-256 of its text as loaded.</summary>
internal sealed record ActivePresetFile(string? Name, string? Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ActivePresetFile))]
internal sealed partial class MpvPresetsJsonContext : JsonSerializerContext;
