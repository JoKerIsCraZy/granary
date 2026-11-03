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
        # They apply after you restart Granary, or with Apply now in Settings after a change made there.
        # A line that starts with # does nothing.
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

    /// <summary>
    /// mpv.conf's text for the editor: the file's own, or the comments a new file starts with when there is none yet
    /// (<see cref="MpvConfigContent.IsNew"/>). Null when it cannot be read, a folder in its place among the reasons.
    /// </summary>
    public MpvConfigContent? ReadConfig()
    {
        if (ConfigFileIsFolder)
        {
            _logger.LogWarning("mpv.conf in the mpv folder is a folder, not a file");
            return null;
        }
        try
        {
            return File.Exists(ConfigFile)
                ? new MpvConfigContent(File.ReadAllText(ConfigFile), IsNew: false)
                : new MpvConfigContent(Template.ReplaceLineEndings("\r\n"), IsNew: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "mpv.conf could not be read");
            return null;
        }
    }

    /// <summary>
    /// Replaces mpv.conf with <paramref name="text"/> as it is, made first when it is missing; false when it could not be
    /// written. A write that fails leaves the file as it was.
    /// </summary>
    public bool WriteConfig(string text) => !ConfigFileIsFolder && EnsureDirectory() && WriteWhole(ConfigFile, text);

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> in UTF-8: whole beside it first, then moved over it, so a
    /// write that fails never leaves half a file. False, and logged, when it could not.
    /// </summary>
    internal bool WriteWhole(string path, string text) => WriteWhole(path, temp => File.WriteAllText(temp, text));

    /// <summary>As <see cref="WriteWhole(string, string)"/>, byte for byte.</summary>
    internal bool WriteWhole(string path, byte[] bytes) => WriteWhole(path, temp => File.WriteAllBytes(temp, bytes));

    private bool WriteWhole(string path, Action<string> write)
    {
        string temp = path + ".tmp";
        try
        {
            write(temp);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{File} could not be written", Path.GetFileName(path));
            Delete(temp);
            return false;
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
            _logger.LogDebug(ex, "A half-written file stays behind as {File}", Path.GetFileName(temp));
        }
    }
}

/// <summary>mpv.conf's text as read for the editor; <paramref name="IsNew"/> when there is no file yet and it is the template.</summary>
public sealed record MpvConfigContent(string Text, bool IsNew);
