using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>An entry of the sidebar that can be hidden and moved: its tag (the sidebar item's), its name and its section.</summary>
/// <param name="Kind">What its icon shows: the tag, or a library's type.</param>
public sealed record SidebarEntry(string Tag, string Label, SidebarSection Section, string Kind);

/// <summary>
/// How the profile wants Granary's sidebar: entries hidden, and the order within each section. Home, Search, Settings and
/// the profile always show. Kept on this PC for each server and profile in <c>sidebar-layout.json</c>: the server's
/// menu setting (<c>nav.primary_menu</c>) is shared with the Mac app and knows neither Requests nor the profile's lists.
/// A file that cannot be read shows the sidebar as it comes; one that cannot be written is logged, and the change still
/// shows for this run.
/// </summary>
public sealed partial class SidebarLayout : ObservableObject
{
    public const string LibraryTagPrefix = "Library:";

    /// <summary>The entries besides the libraries, in the sidebar's own order.</summary>
    public static IReadOnlyList<SidebarEntry> Entries { get; } =
    [
        new("ForYou", "For You", SidebarSection.Discover, "ForYou"),
        new("Requests", "Requests", SidebarSection.Discover, "Requests"),
        new("Calendar", "Calendar", SidebarSection.Discover, "Calendar"),
        new("Notifications", "Notifications", SidebarSection.Discover, "Notifications"),
        new("List:favorites", "Favorites", SidebarSection.YourStuff, "List:favorites"),
        new("List:watchlist", "Watchlist", SidebarSection.YourStuff, "List:watchlist"),
        new("Collections", "Collections", SidebarSection.YourStuff, "Collections"),
        new("List:history", "History", SidebarSection.YourStuff, "List:history"),
    ];

    private readonly string _filePath;
    private readonly ISessionManager _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SidebarLayout> _logger;
    // Saves one at a time, each the layouts as they are when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, ProfileSidebar> _profiles;
    private string? _shownFor;

    public SidebarLayout(string filePath, ISessionManager session, IUiDispatcher dispatcher, ILogger<SidebarLayout> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
        _profiles = Load();
        _shownFor = Key();
        _session.Changed += OnSessionChanged;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "sidebar-layout.json");

    /// <summary>Counts every change of the layout shown, a change by the viewer or another profile's: the sidebar arranges itself anew.</summary>
    [ObservableProperty]
    public partial int Revision { get; private set; }

    /// <summary>The save in flight, for tests.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>The acting profile hid or moved something; Reset shows the sidebar as it comes.</summary>
    public bool IsCustomized => Current is { } layout && (layout.Hidden.Count > 0 || layout.Order.Count > 0);

    public static string TagOf(SidebarLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return LibraryTagPrefix + library.Id;
    }

    public bool IsHidden(string tag) => Current?.Hidden.Contains(tag, StringComparer.Ordinal) == true;

    /// <summary>
    /// A section's entries in the profile's order: those it placed in that order, then any it has not placed (a library
    /// added since) in the order they came.
    /// </summary>
    public IReadOnlyList<string> Arrange(SidebarSection section, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        // A server listing a library twice shows it once.
        tags = [.. tags.Distinct(StringComparer.Ordinal)];
        if (Current?.Order.GetValueOrDefault(NameOf(section)) is not { Count: > 0 } order)
        {
            return tags;
        }
        var rank = order.Select((tag, index) => (tag, index)).GroupBy(p => p.tag, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.Ordinal);
        return [.. tags.Select((tag, index) => (tag, index)).OrderBy(p => rank.GetValueOrDefault(p.tag, order.Count + p.index)).Select(p => p.tag)];
    }

    public Task SetHiddenAsync(string tag, bool hidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (IsHidden(tag) == hidden || Key() is not { } key)
        {
            return Task.CompletedTask;
        }
        var layout = Current ?? ProfileSidebar.Empty;
        var next = layout with { Hidden = hidden ? [.. layout.Hidden, tag] : [.. layout.Hidden.Where(t => t != tag)] };
        return ApplyAsync(key, next);
    }

    /// <summary>Moves an entry one place up (<paramref name="delta"/> -1) or down (+1) among <paramref name="tags"/>, the section as shown.</summary>
    public Task MoveAsync(SidebarSection section, IReadOnlyList<string> tags, string tag, int delta)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var arranged = Arrange(section, tags).ToList();
        int from = arranged.IndexOf(tag);
        int to = from + Math.Sign(delta);
        if (from < 0 || to < 0 || to >= arranged.Count || Key() is not { } key)
        {
            return Task.CompletedTask;
        }
        (arranged[from], arranged[to]) = (arranged[to], arranged[from]);
        var layout = Current ?? ProfileSidebar.Empty;
        var order = new Dictionary<string, IReadOnlyList<string>>(layout.Order, StringComparer.Ordinal) { [NameOf(section)] = arranged };
        if (arranged.SequenceEqual(tags.Distinct(StringComparer.Ordinal)))
        {
            // Back as it comes: nothing of the profile's own in this section.
            order.Remove(NameOf(section));
        }
        return ApplyAsync(key, layout with { Order = order });
    }

    /// <summary>Shows every entry again, in the sidebar's own order.</summary>
    public Task ResetAsync() => Key() is { } key && Current is not null ? ApplyAsync(key, null) : Task.CompletedTask;

    private ProfileSidebar? Current => Key() is { } key ? _profiles.GetValueOrDefault(key) : null;

    private string? Key() => _session is { ActiveServer: { } server, ProfileId: { } profile } ? $"{server.Id}/{profile}" : null;

    private Task ApplyAsync(string key, ProfileSidebar? layout)
    {
        var profiles = new Dictionary<string, ProfileSidebar>(_profiles, StringComparer.Ordinal);
        if (layout is null || (layout.Hidden.Count == 0 && layout.Order.Count == 0))
        {
            profiles.Remove(key);
        }
        else
        {
            profiles[key] = layout;
        }
        _profiles = profiles;
        Revision++;
        OnPropertyChanged(nameof(IsCustomized));
        return Saving = SaveAsync();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (Key() != _shownFor)
        {
            _shownFor = Key();
            Revision++;
            OnPropertyChanged(nameof(IsCustomized));
        }
    });

    private IReadOnlyDictionary<string, ProfileSidebar> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, ProfileSidebar>();
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), SidebarLayoutJsonContext.Default.SidebarLayoutFile);
            return (file?.Profiles ?? new Dictionary<string, SidebarLayoutEntry>()).Where(pair => pair.Value is not null).ToDictionary(
                pair => pair.Key,
                pair => new ProfileSidebar(
                    [.. pair.Value.Hidden ?? []],
                    (pair.Value.Order ?? new Dictionary<string, IReadOnlyList<string>?>()).ToDictionary(o => o.Key, o => (IReadOnlyList<string>)[.. o.Value ?? []], StringComparer.Ordinal)),
                StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: the sidebar shows as it comes.
            _logger.LogWarning(ex, "The sidebar's layout could not be read; it shows as it comes");
            return new Dictionary<string, ProfileSidebar>();
        }
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        string temp = _filePath + ".tmp";
        try
        {
            var file = new SidebarLayoutFile(_profiles.ToDictionary(
                pair => pair.Key,
                pair => new SidebarLayoutEntry([.. pair.Value.Hidden], pair.Value.Order.ToDictionary(o => o.Key, o => (IReadOnlyList<string>?)o.Value))));
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, SidebarLayoutJsonContext.Default.SidebarLayoutFile).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the click's to fail: the sidebar shows the change for this run.
            _logger.LogWarning(ex, "The sidebar's layout could not be saved");
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteFailure)
            {
                _logger.LogDebug(deleteFailure, "A half-written sidebar layout stays behind");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string NameOf(SidebarSection section) => section switch
    {
        SidebarSection.Libraries => "libraries",
        SidebarSection.Discover => "discover",
        _ => "your_stuff",
    };

    private sealed record ProfileSidebar(IReadOnlyList<string> Hidden, IReadOnlyDictionary<string, IReadOnlyList<string>> Order)
    {
        public static readonly ProfileSidebar Empty = new([], new Dictionary<string, IReadOnlyList<string>>());
    }
}

/// <summary><c>sidebar-layout.json</c>: each profile's hidden entries and section orders, by "server id/profile id".</summary>
internal sealed record SidebarLayoutFile(IReadOnlyDictionary<string, SidebarLayoutEntry>? Profiles);

internal sealed record SidebarLayoutEntry(IReadOnlyList<string>? Hidden, IReadOnlyDictionary<string, IReadOnlyList<string>?>? Order);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(SidebarLayoutFile))]
internal sealed partial class SidebarLayoutJsonContext : JsonSerializerContext;
