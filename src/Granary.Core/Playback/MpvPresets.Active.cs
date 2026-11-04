using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// Loading a preset, and going back from it. The first load keeps the viewer's own mpv.conf as it was, byte for byte, in
// .before-preset.conf (or marks that there was none, .before-preset.none); a later load keeps that first backup, so Unload
// always returns to the file from before any preset. .active.json names the preset loaded last and the hash of its text,
// which tells whether mpv.conf was changed since, and it is what makes the backup or the mark count. A load writes it
// before it replaces mpv.conf, in one move, as its last step: until then the viewer's file is in place, and after it nothing
// is left to fail, so no failure leaves the preset in mpv.conf without the way back. Deleting the preset's file leaves all
// of this as it is.
public sealed partial class MpvPresets
{
    private string BackupFile => Path.Combine(Directory, ".before-preset" + Extension);

    private string NoFileMarker => Path.Combine(Directory, ".before-preset.none");

    private string ActiveFile => Path.Combine(Directory, ".active.json");

    /// <summary>For tests: runs once the preset is named, right before mpv.conf is replaced.</summary>
    internal Action? BeforeConfigWrite { get; set; }

    /// <summary>
    /// Replaces mpv.conf with the preset, byte for byte, after keeping the viewer's own file if no preset is loaded yet;
    /// false, with mpv.conf and the way back to it as they were, when any of it cannot be done.
    /// </summary>
    public bool Load(string name)
    {
        if (PathOf(name) is not { } path || _folder.ConfigFileIsFolder)
        {
            return false;
        }
        byte[] preset;
        byte[]? before;
        byte[]? namedBefore;
        try
        {
            preset = MpvConfigFolder.ReadBytes(path);
            before = _folder.ReadConfigBytes();
            namedBefore = File.Exists(ActiveFile) ? MpvConfigFolder.ReadBytes(ActiveFile) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "An mpv preset could not be loaded");
            return false;
        }
        // A backup or a mark holds only with the preset it was made for. Left without one, it is from a session whose Unload
        // could not remove it, and the viewer's own file is the one there now.
        bool backedUp = false;
        if (namedBefore is null || (!File.Exists(BackupFile) && !File.Exists(NoFileMarker)))
        {
            if (!TryDelete(BackupFile, NoFileMarker) || !BackUp(before))
            {
                return false;
            }
            backedUp = true;
        }
        bool namedAsBefore = true;
        if (WriteActive(new ActivePresetFile(name, Hash(preset))))
        {
            BeforeConfigWrite?.Invoke();
            if (_folder.WriteConfig(preset))
            {
                return true;
            }
            namedAsBefore = namedBefore is null ? TryDelete(ActiveFile) : _folder.WriteWhole(ActiveFile, namedBefore);
        }
        // With this preset still named Unload is still offered, and needs the backup: mpv.conf as it is now.
        if (backedUp && namedAsBefore)
        {
            TryDelete(BackupFile, NoFileMarker);
        }
        return false;
    }

    /// <summary>The preset loaded last, and whether mpv.conf differs from it now; null when none is loaded.</summary>
    public MpvActivePreset? Active()
    {
        try
        {
            if (!File.Exists(ActiveFile)
                || JsonSerializer.Deserialize(MpvConfigFolder.ReadText(ActiveFile), MpvPresetsJsonContext.Default.ActivePresetFile) is not { Name: { } name } active)
            {
                return null;
            }
            string? now = File.Exists(_folder.ConfigFile) ? Hash(MpvConfigFolder.ReadBytes(_folder.ConfigFile)) : null;
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
    /// When that fails everything stays for another try. Once it is done the backup and the active preset both go: a backup
    /// left behind does no harm, as a load does not trust it without the active preset, and an active preset that stays is
    /// no success, with Unload still offered to finish.
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
                result = _folder.WriteWhole(_folder.ConfigFile, MpvConfigFolder.ReadBytes(BackupFile)) ? MpvPresetUnload.Restored : MpvPresetUnload.Failed;
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
        if (result == MpvPresetUnload.Failed)
        {
            return result;
        }
        // Each goes whatever becomes of the other: with the backup stuck and the preset still named, the next load would trust
        // that backup, and the Unload after it would decide over a file made since.
        TryDelete(BackupFile, NoFileMarker);
        if (TryDelete(ActiveFile))
        {
            return result;
        }
        return result == MpvPresetUnload.NothingToRestore ? MpvPresetUnload.Failed : MpvPresetUnload.NotCleanedUp;
    }

    // The viewer's own mpv.conf, byte for byte, or the mark that there was none.
    private bool BackUp(byte[]? own)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            return own is null ? _folder.WriteWhole(NoFileMarker, "") : _folder.WriteWhole(BackupFile, own);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "mpv.conf could not be kept before loading a preset");
            return false;
        }
    }

    private bool WriteActive(ActivePresetFile active) =>
        _folder.WriteWhole(ActiveFile, JsonSerializer.Serialize(active, MpvPresetsJsonContext.Default.ActivePresetFile));

    // Each file gone; false, and logged, when one could not be deleted.
    private bool TryDelete(params string[] files)
    {
        bool gone = true;
        foreach (string file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "{File} could not be deleted", Path.GetFileName(file));
                gone = false;
            }
        }
        return gone;
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

    /// <summary>mpv.conf is back, or removed, but the preset is still named: another try finishes it.</summary>
    NotCleanedUp,

    /// <summary>mpv.conf could not be put back; the backup stays.</summary>
    Failed,
}

/// <summary><c>presets\.active.json</c>: the preset's name and the SHA-256 of its text as loaded.</summary>
internal sealed record ActivePresetFile(string? Name, string? Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ActivePresetFile))]
internal sealed partial class MpvPresetsJsonContext : JsonSerializerContext;
