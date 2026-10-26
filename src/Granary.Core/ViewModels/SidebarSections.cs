using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>A sidebar section that folds away by its header.</summary>
public enum SidebarSection
{
    Libraries,
    Discover,
    YourStuff,
}

/// <summary>
/// Which of the sidebar's sections are folded away. LIBRARIES, DISCOVER and YOUR STUFF open and close by their headers, as
/// the web's LIBRARIES does, and stay so across starts: <c>sidebar.json</c> (<c>{ "collapsed": ["discover"] }</c>) is the
/// app's, not a profile's. A file that cannot be read leaves every section open; one that cannot be written is logged, and
/// the sections still fold for this run.
/// </summary>
public sealed partial class SidebarSections : ObservableObject
{
    private readonly string _filePath;
    private readonly ILogger<SidebarSections> _logger;
    // Saves one at a time, each the state as it is when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SidebarSections(string filePath, ILogger<SidebarSections> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        foreach (var section in Load())
        {
            Set(section, expanded: false);
        }
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "sidebar.json");

    [ObservableProperty]
    public partial bool IsLibrariesExpanded { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsDiscoverExpanded { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsYourStuffExpanded { get; private set; } = true;

    public bool IsExpanded(SidebarSection section) => section switch
    {
        SidebarSection.Libraries => IsLibrariesExpanded,
        SidebarSection.Discover => IsDiscoverExpanded,
        _ => IsYourStuffExpanded,
    };

    /// <summary>Opens a folded section or folds an open one, and saves it.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task ToggleAsync(SidebarSection section)
    {
        Set(section, !IsExpanded(section));
        return SaveAsync();
    }

    private void Set(SidebarSection section, bool expanded)
    {
        switch (section)
        {
            case SidebarSection.Libraries:
                IsLibrariesExpanded = expanded;
                break;
            case SidebarSection.Discover:
                IsDiscoverExpanded = expanded;
                break;
            default:
                IsYourStuffExpanded = expanded;
                break;
        }
    }

    // Names this app does not know (a later version's) are left out.
    private IEnumerable<SidebarSection> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), SidebarJsonContext.Default.SidebarFile);
            return (file?.Collapsed ?? []).Select(NameToSection).OfType<SidebarSection>().ToList();
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: the sections are a nicety.
            _logger.LogWarning(ex, "The sidebar's folded sections could not be read; all of them show");
            return [];
        }
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        string temp = _filePath + ".tmp";
        try
        {
            var file = new SidebarFile([.. Enum.GetValues<SidebarSection>().Where(s => !IsExpanded(s)).Select(SectionToName)]);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, SidebarJsonContext.Default.SidebarFile).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the click's to fail: the sections still fold for this run.
            _logger.LogWarning(ex, "The sidebar's folded sections could not be saved");
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
            _logger.LogDebug(ex, "A half-written sidebar file stays behind");
        }
    }

    private static string SectionToName(SidebarSection section) => section switch
    {
        SidebarSection.Libraries => "libraries",
        SidebarSection.Discover => "discover",
        _ => "your_stuff",
    };

    private static SidebarSection? NameToSection(string? name) => name switch
    {
        "libraries" => SidebarSection.Libraries,
        "discover" => SidebarSection.Discover,
        "your_stuff" => SidebarSection.YourStuff,
        _ => null,
    };
}

/// <summary><c>sidebar.json</c>: the sections folded away.</summary>
internal sealed record SidebarFile(IReadOnlyList<string>? Collapsed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(SidebarFile))]
internal sealed partial class SidebarJsonContext : JsonSerializerContext;
