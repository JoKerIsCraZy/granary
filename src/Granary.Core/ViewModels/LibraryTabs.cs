using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The tab each library was last left on, and how its titles were sorted and filtered, so reopening it returns there,
/// as the web's "Remember library view" (<c>ui.remember_library_page_state</c>, on by default) and the Apple app's
/// "Preserve sort &amp; filters" do. Both keep it per profile and device, so this app keeps it in its own data directory:
/// <c>library-tabs.json</c>, per server, profile and library. A file that cannot be read remembers nothing; one that
/// cannot be written is logged, and the library is still remembered for this run.
/// </summary>
public sealed class LibraryTabs
{
    private readonly string _filePath;
    private readonly ILogger<LibraryTabs> _logger;
    // Guards the map, which the UI thread changes while a save may be writing it out.
    private readonly Dictionary<(string Server, string Profile, string Library), Remembered> _libraries = [];
    // Saves one at a time, each the map as it is when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LibraryTabs(string filePath, ILogger<LibraryTabs> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        foreach (var entry in Load())
        {
            if (entry is not { Server: { Length: > 0 } server, Library: { Length: > 0 } library })
            {
                continue;
            }
            var remembered = new Remembered(NameToTab(entry.Tab), BrowseOf(entry));
            if (remembered.Tab is not null || remembered.Browse is not null)
            {
                _libraries[(server, entry.Profile ?? "", library)] = remembered;
            }
        }
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "library-tabs.json");

    /// <summary>The tab the library was last left on for this server and profile, or null when it never was.</summary>
    public LibraryTab? Recall(string serverId, string? profileId, string libraryId)
    {
        lock (_libraries)
        {
            return _libraries.TryGetValue((serverId, profileId ?? "", libraryId), out var known) ? known.Tab : null;
        }
    }

    /// <summary>How the library's titles were last sorted and filtered for this server and profile, or null when never.</summary>
    public LibraryBrowseState? RecallBrowse(string serverId, string? profileId, string libraryId)
    {
        lock (_libraries)
        {
            return _libraries.TryGetValue((serverId, profileId ?? "", libraryId), out var known) ? known.Browse : null;
        }
    }

    /// <summary>Remembers the tab the library is on now, and saves it. Never fails: a save that does not work is logged.</summary>
    public Task RememberAsync(string serverId, string? profileId, string libraryId, LibraryTab tab) =>
        Change(serverId, profileId, libraryId, known => known with { Tab = tab });

    /// <summary>Remembers how the library's titles are sorted and filtered now, and saves it. Never fails.</summary>
    public Task RememberBrowseAsync(string serverId, string? profileId, string libraryId, LibraryBrowseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Change(serverId, profileId, libraryId, known => known with { Browse = state });
    }

    private Task Change(string serverId, string? profileId, string libraryId, Func<Remembered, Remembered> change)
    {
        lock (_libraries)
        {
            var key = (serverId, profileId ?? "", libraryId);
            var known = _libraries.TryGetValue(key, out var found) ? found : new Remembered(null, null);
            var next = change(known);
            if (next == known)
            {
                return Task.CompletedTask;
            }
            _libraries[key] = next;
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
            // Whatever the file or the disk does, the app starts: every library opens on Recommended, by title.
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
            lock (_libraries)
            {
                file = new LibraryTabsFile([.. _libraries.Select(pair => Entry(pair.Key, pair.Value))]);
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
            // Never the tab switch's or the sort's to fail: the library is still remembered for this run.
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

    private static LibraryTabEntry Entry((string Server, string Profile, string Library) key, Remembered remembered) => new(
        key.Server, key.Profile.Length > 0 ? key.Profile : null, key.Library, remembered.Tab is { } tab ? TabToName(tab) : null)
    {
        Sort = remembered.Browse?.SortField,
        Descending = remembered.Browse?.Descending,
        Genre = remembered.Browse?.Genre,
        ContentRatings = remembered.Browse is { ContentRatings.Count: > 0 } browse ? browse.ContentRatings : null,
        YearFrom = remembered.Browse?.YearFrom,
        YearTo = remembered.Browse?.YearTo,
    };

    // An entry from before sort and filters were kept has none: the library opens by title.
    private static LibraryBrowseState? BrowseOf(LibraryTabEntry entry) => entry.Sort is { Length: > 0 } sort
        ? new LibraryBrowseState(sort, entry.Descending ?? false, entry.Genre, [.. entry.ContentRatings?.OfType<string>() ?? []], entry.YearFrom, entry.YearTo)
        : null;

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

    private sealed record Remembered(LibraryTab? Tab, LibraryBrowseState? Browse);
}

/// <summary><c>library-tabs.json</c>: the tab each library was last left on, and its sort and filters.</summary>
internal sealed record LibraryTabsFile(IReadOnlyList<LibraryTabEntry?>? Tabs);

internal sealed record LibraryTabEntry(string? Server, string? Profile, string? Library, string? Tab)
{
    /// <summary>A <see cref="LibrarySortOption.Field"/>; absent in a file from before sorts were kept.</summary>
    public string? Sort { get; init; }

    public bool? Descending { get; init; }

    public string? Genre { get; init; }

    public IReadOnlyList<string?>? ContentRatings { get; init; }

    public int? YearFrom { get; init; }

    public int? YearTo { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LibraryTabsFile))]
internal sealed partial class LibraryTabsJsonContext : JsonSerializerContext;
