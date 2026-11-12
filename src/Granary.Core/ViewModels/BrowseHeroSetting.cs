using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Settings → Navigation &amp; Cards → Pages, the hero at the top of Home, For You and Requests' Discover
/// (<see cref="BrowseHeroViewModel"/>):
/// <list type="bullet">
/// <item>
/// Title preview at the top (<see cref="IsShown"/>): whether the pages open with the large picture of the title pointed at,
/// or start with their rows. On by default. Granary's own switch: the hero is Granary's, and Silo's clients have none to turn
/// off. It leaves Home's banner alone, which a row marked as the hero on Silo's web brings, as on the web.
/// </item>
/// <item>
/// Hero size (<see cref="Size"/>): Granary's compact hero, the default, or one as tall as the banner of Silo's web
/// (<see cref="BrowseHeroLayout.For"/>). Home's banner follows it too.
/// </item>
/// </list>
/// Both are kept on this PC for each server and profile in <c>browse-hero.json</c>, as the sidebar's layout is
/// (<see cref="SidebarLayout"/>): the server keeps no such settings. A file that cannot be read shows the hero, compact; one
/// that cannot be written is logged, and the choice still holds for this run.
/// </summary>
public sealed partial class BrowseHeroSetting : ObservableObject
{
    private readonly string _filePath;
    private readonly ISessionManager _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<BrowseHeroSetting> _logger;
    // Saves one at a time, each the profiles as they are when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The profiles that turned the title preview off, and those that chose the full size, by "server id/profile id".
    // Replaced, never changed, so a save reads them whole.
    private IReadOnlySet<string> _hiddenFor;
    private IReadOnlySet<string> _fullFor;

    public BrowseHeroSetting(string filePath, ISessionManager session, IUiDispatcher dispatcher, ILogger<BrowseHeroSetting> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
        (_hiddenFor, _fullFor) = Load();
        _session.Changed += OnSessionChanged;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "browse-hero.json");

    /// <summary>The acting profile's pages open with the title preview, as they do without a profile.</summary>
    public bool IsShown => Key() is not { } key || !_hiddenFor.Contains(key);

    /// <summary>The acting profile's hero size; compact until it chooses, and without a profile.</summary>
    public BrowseHeroSize Size => Key() is { } key && _fullFor.Contains(key) ? BrowseHeroSize.Full : BrowseHeroSize.Compact;

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
        // Weighed for the profile read above: the session is not read a second time, where another profile could be acting.
        if (shown == !_hiddenFor.Contains(key))
        {
            return Task.CompletedTask;
        }
        _hiddenFor = With(_hiddenFor, key, !shown);
        OnPropertyChanged(nameof(IsShown));
        return Saving = SaveAsync();
    }

    /// <summary>
    /// Sets the acting profile's hero size, and saves it; without a profile there is none to keep it for, and it stays
    /// compact. Never fails: a save that does not work is logged.
    /// </summary>
    public Task SetSizeAsync(BrowseHeroSize size)
    {
        // Weighed for the profile read here: the session is not read a second time, where another profile could be acting.
        if (Key() is not { } key || (size == BrowseHeroSize.Full) == _fullFor.Contains(key))
        {
            return Task.CompletedTask;
        }
        _fullFor = With(_fullFor, key, size == BrowseHeroSize.Full);
        OnPropertyChanged(nameof(Size));
        return Saving = SaveAsync();
    }

    private string? Key() => _session is { ActiveServer: { } server, ProfileId: { } profile } ? $"{server.Id}/{profile}" : null;

    // The profiles with or without the one of key, as a new set.
    private static IReadOnlySet<string> With(IReadOnlySet<string> keys, string key, bool included) => included
        ? keys.Append(key).ToHashSet(StringComparer.Ordinal)
        : keys.Where(k => k != key).ToHashSet(StringComparer.Ordinal);

    // Another profile has its own choices; Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(Size));
    });

    private (IReadOnlySet<string> HiddenFor, IReadOnlySet<string> FullFor) Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), BrowseHeroJsonContext.Default.BrowseHeroFile);
                return (Keys(file?.HiddenFor), Keys(file?.FullFor));
            }
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: the pages show their hero, compact.
            _logger.LogWarning(ex, "The hero's settings could not be read; the pages show it, compact");
        }
        return (Keys(null), Keys(null));
    }

    private static IReadOnlySet<string> Keys(IReadOnlyList<string?>? keys) => (keys ?? []).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        // Its own temporary file: a second Granary on the same data folder saving at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var file = new BrowseHeroFile([.. _hiddenFor.Order(StringComparer.Ordinal)], [.. _fullFor.Order(StringComparer.Ordinal)]);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, BrowseHeroJsonContext.Default.BrowseHeroFile).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the setting's to fail: the choice holds for this run.
            _logger.LogWarning(ex, "The hero's settings could not be saved");
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteFailure)
            {
                _logger.LogDebug(deleteFailure, "A half-written file of the hero's settings stays behind");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// <c>browse-hero.json</c>: the profiles, by "server id/profile id", whose pages start with their rows, and those whose
/// hero is the full size. Both may be missing: a file from before the size could be chosen has no <c>full_for</c>, which
/// means compact for everyone.
/// </summary>
internal sealed record BrowseHeroFile(IReadOnlyList<string?>? HiddenFor, IReadOnlyList<string?>? FullFor = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(BrowseHeroFile))]
internal sealed partial class BrowseHeroJsonContext : JsonSerializerContext;
