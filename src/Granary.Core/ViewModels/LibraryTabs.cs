using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The tab each library was last left on, so reopening it returns there, as the web's "Remember library view"
/// (<c>ui.remember_library_page_state</c>, on by default) does. The web keeps it per profile and device, so this app keeps
/// it in its own data directory: <c>library-tabs.json</c>, per server, profile and library. A file that cannot be read
/// remembers nothing; one that cannot be written is logged, and the tabs are still remembered for this run.
/// </summary>
public sealed class LibraryTabs
{
    private readonly string _filePath;
    private readonly ILogger<LibraryTabs> _logger;
    // Guards the map, which the UI thread changes while a save may be writing it out.
    private readonly Dictionary<(string Server, string Profile, string Library), LibraryTab> _tabs = [];
    // Saves one at a time, each the map as it is when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LibraryTabs(string filePath, ILogger<LibraryTabs> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        foreach (var entry in Load())
        {
            if (entry is { Server: { Length: > 0 } server, Library: { Length: > 0 } library } && NameToTab(entry.Tab) is { } tab)
            {
                _tabs[(server, entry.Profile ?? "", library)] = tab;
            }
        }
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "library-tabs.json");

    /// <summary>The tab the library was last left on for this server and profile, or null when it never was.</summary>
    public LibraryTab? Recall(string serverId, string? profileId, string libraryId)
    {
        lock (_tabs)
        {
            return _tabs.TryGetValue((serverId, profileId ?? "", libraryId), out var tab) ? tab : null;
        }
    }

    /// <summary>Remembers the tab the library is on now, and saves it. Never fails: a save that does not work is logged.</summary>
    public Task RememberAsync(string serverId, string? profileId, string libraryId, LibraryTab tab)
    {
        lock (_tabs)
        {
            var key = (serverId, profileId ?? "", libraryId);
            if (_tabs.TryGetValue(key, out var known) && known == tab)
            {
                return Task.CompletedTask;
            }
            _tabs[key] = tab;
        }
        return SaveAsync();
    }

    private IReadOnlyList<LibraryTabEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), LibraryTabsJsonContext.Default.LibraryTabsFile);
            return file?.Tabs?.OfType<LibraryTabEntry>().ToList() ?? [];
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: every library opens on Recommended.
            _logger.LogWarning(ex, "The libraries' last tabs could not be read; each library opens on Recommended");
            return [];
        }
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        string temp = _filePath + ".tmp";
        try
        {
            LibraryTabsFile file;
            lock (_tabs)
            {
                file = new LibraryTabsFile([.. _tabs.Select(pair => new LibraryTabEntry(
                    pair.Key.Server, pair.Key.Profile.Length > 0 ? pair.Key.Profile : null, pair.Key.Library, TabToName(pair.Value)))]);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, LibraryTabsJsonContext.Default.LibraryTabsFile).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the tab switch's to fail: the tab is still remembered for this run.
            _logger.LogWarning(ex, "The libraries' last tabs could not be saved");
            TryDelete(temp);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A half-written library tabs file stays behind");
        }
    }

    private static string TabToName(LibraryTab tab) => tab switch
    {
        LibraryTab.Library => "library",
        LibraryTab.Collections => "collections",
        _ => "recommended",
    };

    // The web's tab names (LibraryPageTab); names this app does not know are left out.
    private static LibraryTab? NameToTab(string? name) => name switch
    {
        "recommended" => LibraryTab.Recommended,
        "library" => LibraryTab.Library,
        "collections" => LibraryTab.Collections,
        _ => null,
    };
}

/// <summary><c>library-tabs.json</c>: the tab each library was last left on.</summary>
internal sealed record LibraryTabsFile(IReadOnlyList<LibraryTabEntry?>? Tabs);

internal sealed record LibraryTabEntry(string? Server, string? Profile, string? Library, string? Tab);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LibraryTabsFile))]
internal sealed partial class LibraryTabsJsonContext : JsonSerializerContext;
