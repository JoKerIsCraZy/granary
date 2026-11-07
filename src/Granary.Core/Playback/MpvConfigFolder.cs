using System.Text;
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
        # They apply from the next title you play.
        # A line that starts with # does nothing.
        #
        # ~~/ is this folder, for example: glsl-shaders=~~/shaders/FSRCNNX_x2_8-0-4-1.glsl
        # Profiles, profile-cond and include work as in mpv.
        #
        # Granary keeps the options it needs to show the video and follow your Silo server's choices, such as vo,
        # gpu-api, keep-open and idle. Its log says which of yours it leaves out. Scripts and input.conf are not used.
        # A file that sets an option that could take the stream's address out of Granary, such as log-file, scripts,
        # input-ipc-server or http-proxy, is not used at all: Settings and the log say why.
        # A wrong option can make playback worse. Delete this file to go back to normal.
        #
        # deband=yes
        # scale=ewa_lanczossharp

        """;

    // Enough for a read of these small files to end; about 200 ms in all. All writes run off the UI thread.
    private const int MoveTries = 6;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(40);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ILogger<MpvConfigFolder> _logger;

    /// <summary>For tests: runs before a write that the file being open got in the way of tries again.</summary>
    internal Action? BeforeMoveRetry { get; set; }

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
    /// (<see cref="MpvConfigContent.IsNew"/>). A file that is not UTF-8 has no text to edit
    /// (<see cref="MpvConfigContent.IsNotText"/>): saving it decoded would damage it. Null when it cannot be read, a folder
    /// in its place among the reasons: that is no missing file, and nothing may replace it.
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
            if (ReadConfigBytes() is not { } bytes)
            {
                return new MpvConfigContent(Template.ReplaceLineEndings("\r\n"), IsNew: true);
            }
            return StrictText(bytes) is { } text
                ? new MpvConfigContent(text, IsNew: false)
                : new MpvConfigContent("", IsNew: false, IsNotText: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "mpv.conf could not be read");
            return null;
        }
    }

    /// <summary>
    /// mpv.conf's bytes; null only when it is not there. One that is there but cannot be read throws: Windows' File.Exists
    /// says false for a file it may not look at, so it does not tell the two apart.
    /// </summary>
    internal byte[]? ReadConfigBytes()
    {
        try
        {
            return ReadBytes(ConfigFile);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // The file as UTF-8, a byte order mark left out; null when it is anything else. A NUL is no text either: UTF-16 without a
    // byte order mark passes as UTF-8.
    private static string? StrictText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
        {
            return null;
        }
        try
        {
            return StrictUtf8.GetString(bytes.StartsWith(Encoding.UTF8.Preamble) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// A file's bytes, read so that a delete of it meanwhile goes ahead rather than failing on this read. A write has to wait
    /// for the read to end, which <see cref="WriteWhole(string, string)"/> does.
    /// </summary>
    internal static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// A config file as the player checks and compares it, read as <see cref="ReadBytes"/>: its UTF-8 text, or else each byte
    /// as a character of its own, as mpv takes the bytes as they are and a change in any of them is a change.
    /// </summary>
    public static string ReadForCheck(string path)
    {
        byte[] bytes = ReadBytes(path);
        return StrictText(bytes) ?? Encoding.Latin1.GetString(bytes);
    }

    /// <summary>As <see cref="ReadBytes"/>, as text: UTF-8 unless a byte order mark says otherwise, as File.ReadAllText reads it.</summary>
    internal static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Replaces mpv.conf with <paramref name="text"/> as it is, made first when it is missing; false when it could not be
    /// written. A write that fails leaves the file as it was.
    /// </summary>
    public bool WriteConfig(string text) => !ConfigFileIsFolder && EnsureDirectory() && WriteWhole(ConfigFile, text);

    /// <summary>As <see cref="WriteConfig(string)"/>, byte for byte.</summary>
    internal bool WriteConfig(byte[] bytes) => !ConfigFileIsFolder && EnsureDirectory() && WriteWhole(ConfigFile, bytes);

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> in UTF-8: whole beside it first, then moved over it, so a
    /// write that fails never leaves half a file. False, and logged, when it could not.
    /// </summary>
    internal bool WriteWhole(string path, string text) => WriteWhole(path, temp => File.WriteAllText(temp, text));

    /// <summary>As <see cref="WriteWhole(string, string)"/>, byte for byte.</summary>
    internal bool WriteWhole(string path, byte[] bytes) => WriteWhole(path, temp => File.WriteAllBytes(temp, bytes));

    private bool WriteWhole(string path, Action<string> write)
    {
        // Its own temporary name, in the same folder so the move replaces the file at once: a leftover of an earlier write, or
        // a second Granary on this data folder writing now, cannot get in its way.
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            write(temp);
            MoveOver(temp, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{File} could not be written", Path.GetFileName(path));
            Delete(temp);
            return false;
        }
    }

    // Windows refuses to replace a file while anything has it open, whatever that shares: a read Granary started by itself
    // (the settings row, the player's check) or a virus scanner's. Those end in a moment; a file held for good still fails.
    // So does one whose last write or size changed while it waited: that is another program's save, and it stays.
    private void MoveOver(string temp, string path)
    {
        var before = Stamp(path);
        for (int tries = 1; ; tries++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && tries < MoveTries)
            {
                BeforeMoveRetry?.Invoke();
                Thread.Sleep(MoveRetryDelay);
            }
            if (Stamp(path) != before)
            {
                throw new IOException("The file changed while the write waited for it.");
            }
        }
    }

    // A file's last write and size, which a read leaves alone and a save does not. Readable while another program has it
    // open, though Windows may only show a change once that program lets go.
    private static (DateTime LastWrite, long Length) Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
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

/// <summary>
/// mpv.conf's text as read for the editor; <paramref name="IsNew"/> when there is no file yet and it is the template,
/// <paramref name="IsNotText"/> when the file is not UTF-8 and has no text to edit.
/// </summary>
public sealed record MpvConfigContent(string Text, bool IsNew, bool IsNotText = false);
