using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Settings → Navigation &amp; Cards → Title preview at the top: whether Home, For You and Requests' Discover open with the
/// large picture of the title pointed at (<see cref="BrowseHeroViewModel"/>), or start with their rows. On by default.
/// Granary's own switch: the hero is Granary's, and Silo's clients have none to turn off. It leaves Home's banner alone,
/// which a row marked as the hero on Silo's web brings, as on the web. Kept on this PC for each server and profile in
/// <c>browse-hero.json</c>, as the sidebar's layout is (<see cref="SidebarLayout"/>): the server keeps no such setting. A
/// file that cannot be read shows the hero; one that cannot be written is logged, and the choice still holds for this run.
/// </summary>
public sealed partial class BrowseHeroSetting : ObservableObject
{
    private readonly string _filePath;
    private readonly ISessionManager _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<BrowseHeroSetting> _logger;
    // Saves one at a time, each the profiles as they are when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The profiles that turned it off, by "server id/profile id". Replaced, never changed, so a save reads it whole.
    private IReadOnlySet<string> _hiddenFor;

    public BrowseHeroSetting(string filePath, ISessionManager session, IUiDispatcher dispatcher, ILogger<BrowseHeroSetting> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
        _hiddenFor = Load();
        _session.Changed += OnSessionChanged;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "browse-hero.json");

    /// <summary>The acting profile's pages open with the title preview, as they do without a profile.</summary>
    public bool IsShown => Key() is not { } key || !_hiddenFor.Contains(key);

    /// <summary>The save in flight, for tests.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>Turns the title preview on or off for the acting profile, and saves it. Never fails: a save that does not work is logged.</summary>
    public Task SetShownAsync(bool shown)
    {
        if (Key() is not { } key)
        {
            // No profile to keep it for: the switch goes back to what the pages show, once its own change is done. Told from
            // inside the change, a two-way binding would not take it, and the switch would stay where it was moved.
            _dispatcher.Post(() => OnPropertyChanged(nameof(IsShown)));
            return Task.CompletedTask;
        }
        if (shown == IsShown)
        {
            return Task.CompletedTask;
        }
        _hiddenFor = shown
            ? _hiddenFor.Where(k => k != key).ToHashSet(StringComparer.Ordinal)
            : _hiddenFor.Append(key).ToHashSet(StringComparer.Ordinal);
        OnPropertyChanged(nameof(IsShown));
        return Saving = SaveAsync();
    }

    private string? Key() => _session is { ActiveServer: { } server, ProfileId: { } profile } ? $"{server.Id}/{profile}" : null;

    // Another profile has its own choice; Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => OnPropertyChanged(nameof(IsShown)));

    private IReadOnlySet<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), BrowseHeroJsonContext.Default.BrowseHeroFile);
            return (file?.HiddenFor ?? []).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: the pages show their hero.
            _logger.LogWarning(ex, "The title preview's switch could not be read; the pages show it");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        // Its own temporary file: a second Granary on the same data folder saving at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var file = new BrowseHeroFile([.. _hiddenFor.Order(StringComparer.Ordinal)]);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, BrowseHeroJsonContext.Default.BrowseHeroFile).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the switch's to fail: the choice holds for this run.
            _logger.LogWarning(ex, "The title preview's switch could not be saved");
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteFailure)
            {
                _logger.LogDebug(deleteFailure, "A half-written title preview file stays behind");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary><c>browse-hero.json</c>: the profiles, by "server id/profile id", whose pages start with their rows.</summary>
internal sealed record BrowseHeroFile(IReadOnlyList<string?>? HiddenFor);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(BrowseHeroFile))]
internal sealed partial class BrowseHeroJsonContext : JsonSerializerContext;
