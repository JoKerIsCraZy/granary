using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>
/// The folder for the viewer's own mpv configuration on this PC: <c>mpv.conf</c>, in mpv's own syntax, and the shaders or
/// other files it names (<c>~~/</c> in mpv.conf is this folder). The player reads it when mpv starts; Settings → Playback
/// → This PC opens it.
/// </summary>
public sealed class MpvConfigFolder
{
    public const string ConfigFileName = "mpv.conf";

    /// <summary>What a new mpv.conf says, all of it comments: it changes nothing until the viewer adds an option.</summary>
    internal const string Template = """
        # Your own mpv options for Granary, in mpv's syntax: https://mpv.io/manual/stable/#configuration-files
        # They apply after you restart Granary. A line that starts with # does nothing.
        #
        # ~~/ is this folder, for example: glsl-shaders=~~/shaders/FSRCNNX_x2_8-0-4-1.glsl
        # Profiles, profile-cond and include work as in mpv.
        #
        # Granary keeps the options it needs to show the video and follow your Silo server's choices, such as vo,
        # gpu-api, keep-open and idle. Its log says which of yours it leaves out. Scripts and input.conf are not used.
        # A wrong option can make playback worse. Delete this file to go back to normal.
        #
        # deband=yes
        # scale=ewa_lanczossharp

        """;

    private readonly ILogger<MpvConfigFolder> _logger;

    public MpvConfigFolder(string directory, ILogger<MpvConfigFolder> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _logger = logger;
    }

    /// <summary><c>mpv</c> in the data directory: <c>%LOCALAPPDATA%\Granary\mpv</c>.</summary>
    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "mpv");

    public string Directory { get; }

    public string ConfigFile => Path.Combine(Directory, ConfigFileName);

    /// <summary>Makes the folder when it is missing; false when it cannot be made.</summary>
    public bool EnsureDirectory()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The mpv folder could not be made");
            return false;
        }
    }

    /// <summary>A folder stands where mpv.conf belongs: mpv cannot read it, and Granary cannot make the file.</summary>
    public bool ConfigFileIsFolder => System.IO.Directory.Exists(ConfigFile);

    /// <summary>
    /// mpv.conf's path, the file made with a few comments when it is missing; one that exists is left as it is. Null when it
    /// cannot be made, and when a folder has its name (<see cref="ConfigFileIsFolder"/>).
    /// </summary>
    public string? EnsureConfigFile()
    {
        if (File.Exists(ConfigFile))
        {
            return ConfigFile;
        }
        if (ConfigFileIsFolder)
        {
            _logger.LogWarning("mpv.conf in the mpv folder is a folder, not a file");
            return null;
        }
        if (!EnsureDirectory())
        {
            return null;
        }
        // Written whole beside it first: a write that fails leaves no empty mpv.conf behind.
        string temp = ConfigFile + ".tmp";
        try
        {
            File.WriteAllText(temp, Template.ReplaceLineEndings("\r\n"));
            // Not over a file that appeared meanwhile: that one is the viewer's and stays.
            File.Move(temp, ConfigFile, overwrite: false);
            return ConfigFile;
        }
        catch (IOException) when (File.Exists(ConfigFile))
        {
            Delete(temp);
            return ConfigFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "mpv.conf could not be made");
            Delete(temp);
            return null;
        }
    }

    private void Delete(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "A half-written mpv.conf stays behind as {File}", Path.GetFileName(temp));
        }
    }
}
