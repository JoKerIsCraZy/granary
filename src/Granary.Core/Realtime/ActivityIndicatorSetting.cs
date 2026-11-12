using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

/// <summary>
/// Settings → Navigation &amp; Cards → Server activity: whether an administrator has the activity indicator
/// (<see cref="ViewModels.Admin.ServerActivityViewModel"/>). On by default. Off, it shows nowhere, the admin area
/// included, and nothing is followed for it: <see cref="RealtimeEvents"/> asks for no administrator's channel, so
/// <see cref="ServerActivity"/> reads nothing. Granary's own switch: on Silo's web the indicator is always there.
/// </summary>
/// <remarks>
/// Kept on this PC for each server and profile in <c>activity-indicator.json</c>, as the title preview's switch is
/// (<see cref="ViewModels.BrowseHeroSetting"/>): the server keeps no such setting. A file that cannot be read leaves the
/// indicator on; one that cannot be written is logged, and the choice still holds for this run.
/// </remarks>
public sealed partial class ActivityIndicatorSetting : ObservableObject
{
    private readonly string _filePath;
    private readonly ISessionManager _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ActivityIndicatorSetting> _logger;
    // Saves one at a time, each the profiles as they are when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The profiles that turned the indicator off, by "server id/profile id". Replaced, never changed: a save reads them
    // whole, and so does the events stream, on whichever thread the session changes on.
    private volatile IReadOnlySet<string> _hiddenFor;

    public ActivityIndicatorSetting(string filePath, ISessionManager session, IUiDispatcher dispatcher, ILogger<ActivityIndicatorSetting> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
        _hiddenFor = Load();
        _session.Changed += OnSessionChanged;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "activity-indicator.json");

    /// <summary>The acting profile has its indicator, as everyone has without a profile.</summary>
    public bool IsShown => Key() is not { } key || !_hiddenFor.Contains(key);

    /// <summary>
    /// An administrator's account is signed in on a profile: there is an indicator to switch. Whether the profile acts as
    /// the administrator is the server's to say, with each connection.
    /// </summary>
    public bool IsOffered => _session is { State: SessionState.Ready, Account.IsAdmin: true } && Key() is not null;

    /// <summary>The save in flight, for tests.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>Whether that profile has its indicator, for a caller that read the session itself.</summary>
    public bool IsShownFor(string serverId, string profileId) => !_hiddenFor.Contains(KeyOf(serverId, profileId));

    /// <summary>Turns the indicator on or off for the acting profile, and saves it. Never fails: a save that does not work is logged.</summary>
    public Task SetShownAsync(bool shown)
    {
        if (Key() is not { } key)
        {
            // No profile to keep it for: the switch goes back, once its own change is done. Told from inside the change,
            // a two-way binding would not take it, and the switch would stay where it was moved.
            _dispatcher.Post(() => OnPropertyChanged(nameof(IsShown)));
            return Task.CompletedTask;
        }
        // Weighed for the profile read above: the session is not read a second time, where another profile could be acting.
        if (shown == !_hiddenFor.Contains(key))
        {
            return Task.CompletedTask;
        }
        _hiddenFor = shown
            ? _hiddenFor.Where(k => k != key).ToHashSet(StringComparer.Ordinal)
            : _hiddenFor.Append(key).ToHashSet(StringComparer.Ordinal);
        OnPropertyChanged(nameof(IsShown));
        return Saving = SaveAsync();
    }

    private string? Key() => _session is { ActiveServer: { } server, ProfileId: { } profile } ? KeyOf(server.Id, profile) : null;

    private static string KeyOf(string serverId, string profileId) => $"{serverId}/{profileId}";

    // Another profile has its own choice, and another account may be no administrator; Changed can come from a background
    // thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(IsOffered));
    });

    private IReadOnlySet<string> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), ActivityIndicatorJsonContext.Default.ActivityIndicatorFile);
                return (file?.HiddenFor ?? []).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: the indicator is on.
            _logger.LogWarning(ex, "The activity indicator's setting could not be read; it is on");
        }
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        // Its own temporary file: a second Granary on the same data folder saving at the same moment cannot write into it.
        string temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var file = new ActivityIndicatorFile([.. _hiddenFor.Order(StringComparer.Ordinal)]);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, ActivityIndicatorJsonContext.Default.ActivityIndicatorFile).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the setting's to fail: the choice holds for this run.
            _logger.LogWarning(ex, "The activity indicator's setting could not be saved");
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteFailure)
            {
                _logger.LogDebug(deleteFailure, "A half-written file of the activity indicator's setting stays behind");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary><c>activity-indicator.json</c>: the profiles, by "server id/profile id", that turned their indicator off.</summary>
internal sealed record ActivityIndicatorFile(IReadOnlyList<string?>? HiddenFor);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(ActivityIndicatorFile))]
internal sealed partial class ActivityIndicatorJsonContext : JsonSerializerContext;
