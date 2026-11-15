using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Texts;

/// <summary>
/// The app language kept on this PC for the next start (<c>app-language.json</c>): the profile's <c>ui.app_language</c> as
/// Granary last read or saved it, null to follow Windows. As Silo's apps keep the language they last applied for the screens
/// before sign-in (silo-server docs/architecture/i18n.md, "Precedence and catalog gating"), Granary starts in it before it
/// knows who is watching; the texts are loaded once, at start, so a change applies from the next one.
/// </summary>
/// <remarks>
/// A file that cannot be read follows Windows; one that cannot be written is logged, and the start after it follows what
/// the file held before.
/// </remarks>
public sealed class AppLanguageFile
{
    // Longer than any language tag a person would store; the server keeps open tags.
    private const int MaxTagLength = 64;

    private readonly string _filePath;
    private readonly ILogger<AppLanguageFile> _logger;
    // Saves one at a time, each the value as it is when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string? _kept;

    public AppLanguageFile(string filePath, ILogger<AppLanguageFile> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        AtStart = _kept = Load();
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "app-language.json");

    /// <summary>What the file held as Granary started: the language this run's texts were chosen by.</summary>
    public string? AtStart { get; }

    /// <summary>What the next start follows.</summary>
    public string? Kept => _kept;

    /// <summary>Keeps <paramref name="tag"/> for the next start, null to follow Windows. Never fails: a save that does not work is logged.</summary>
    public async Task KeepAsync(string? tag)
    {
        if (tag == _kept)
        {
            return;
        }
        _kept = tag;
        await _gate.WaitAsync().ConfigureAwait(false);
        // Its own temporary file: a second Granary on the same data folder saving at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new AppLanguageFileData(_kept), AppLanguageJsonContext.Default.AppLanguageFileData)
                    .ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The app language for the next start could not be saved");
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteFailure)
            {
                _logger.LogDebug(deleteFailure, "A half-written file of the app language stays behind");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }
            var data = JsonSerializer.Deserialize(File.ReadAllText(_filePath), AppLanguageJsonContext.Default.AppLanguageFileData);
            return data?.AppLanguage?.Trim() is { Length: > 0 and <= MaxTagLength } tag ? tag : null;
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: in Windows' language.
            _logger.LogWarning(ex, "The app language kept for this start could not be read; Windows' languages apply");
            return null;
        }
    }
}

/// <summary><c>app-language.json</c>: the profile's <c>ui.app_language</c> as last read or saved; null follows Windows.</summary>
internal sealed record AppLanguageFileData(string? AppLanguage);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(AppLanguageFileData))]
internal sealed partial class AppLanguageJsonContext : JsonSerializerContext;
