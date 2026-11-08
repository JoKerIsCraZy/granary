using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>The player buttons of the acting profile on this PC (<see cref="PlayerButtonsLayout"/>), for an open player to follow.</summary>
public interface IPlayerButtonsLayout
{
    /// <summary>The acting profile's bar, as it is now; read from any thread.</summary>
    PlayerBar Current { get; }

    /// <summary>
    /// Raised with the new bar on the thread that changed it, and on the UI thread when another profile acts, so an open
    /// player follows each change in order, even after the next one.
    /// </summary>
    event EventHandler<PlayerBar>? Changed;
}

/// <summary>
/// Which buttons the right of the player's bar shows, and in which order, for each server and profile on this PC, in
/// <c>player-buttons.json</c>, as <see cref="ViewModels.SidebarLayout"/> keeps the sidebar. Granary's own: Silo's clients
/// have no such editor, and the server keeps nothing of it. A file that cannot be read shows today's bar; one that cannot
/// be written is logged, and the change still shows for this run.
/// <para>
/// This PC's Sleep timer switch from before the editor (<c>sleep_timer_button: false</c> in <c>playback-device.json</c>)
/// is taken over once, when there is no file yet: every profile then starts with the Sleep timer hidden, until it changes
/// its buttons or resets them.
/// </para>
/// </summary>
public sealed partial class PlayerButtonsLayout : ObservableObject, IPlayerButtonsLayout
{
    private readonly string _filePath;
    private readonly ISessionManager _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<PlayerButtonsLayout> _logger;
    // Saves one at a time, each the layouts as they are when its turn comes, so the last one written is the latest.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The bar of a profile without one of its own: today's, or with the Sleep timer hidden as this PC's switch had it.
    private readonly PlayerBar _start;
    private volatile IReadOnlyDictionary<string, PlayerBar> _profiles;
    private string? _shownFor;

    public PlayerButtonsLayout(string filePath, ISessionManager session, IUiDispatcher dispatcher, bool sleepTimerSwitchOff,
        ILogger<PlayerButtonsLayout> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
        bool isNew = !File.Exists(_filePath);
        (_profiles, var startHidden) = Load();
        if (isNew && sleepTimerSwitchOff)
        {
            startHidden = [PlayerButton.SleepTimer];
        }
        _start = new PlayerBar([], startHidden);
        if (isNew && sleepTimerSwitchOff)
        {
            // Written at once, so it is taken over this once: from now on the file decides.
            _logger.LogInformation("The player's Sleep timer starts hidden, as this PC's switch had it");
            Saving = SaveAsync();
        }
        _shownFor = Key();
        _session.Changed += OnSessionChanged;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "player-buttons.json");

    public event EventHandler<PlayerBar>? Changed;

    /// <summary>Counts every change of the bar shown, a change by the viewer or another profile's: the settings show it anew.</summary>
    [ObservableProperty]
    public partial int Revision { get; private set; }

    /// <summary>The save in flight, for tests.</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>Waited for before each write, inside it, so a test can hold a save back or make it fail.</summary>
    internal Func<Task> BeforeWrite { get; set; } = static () => Task.CompletedTask;

    public PlayerBar Current => Key() is { } key && _profiles.TryGetValue(key, out var bar) ? bar : _start;

    /// <summary>The acting profile's bar is not today's; Reset brings that back.</summary>
    public bool IsCustomized => !Current.IsDefault;

    public Task SetShownAsync(PlayerButton button, bool shown) =>
        Current.Shows(button) == shown ? Task.CompletedTask : ApplyAsync(Current.WithShown(button, shown));

    /// <summary>Moves a button one place earlier (<paramref name="delta"/> -1) or later (+1).</summary>
    public Task MoveAsync(PlayerButton button, int delta)
    {
        var moved = Current.WithMoved(button, delta);
        return moved.Equals(Current) ? Task.CompletedTask : ApplyAsync(moved);
    }

    /// <summary>Today's bar again: every button, in the bar's own order.</summary>
    public Task ResetAsync() => Current.IsDefault ? Task.CompletedTask : ApplyAsync(PlayerBar.Default);

    private string? Key() => _session is { ActiveServer: { } server, ProfileId: { } profile } ? $"{server.Id}/{profile}" : null;

    // Without a profile acting there is no one to keep it for: nothing changes.
    private Task ApplyAsync(PlayerBar bar)
    {
        if (Key() is not { } key)
        {
            return Task.CompletedTask;
        }
        var profiles = new Dictionary<string, PlayerBar>(_profiles, StringComparer.Ordinal);
        if (bar.Equals(_start))
        {
            profiles.Remove(key);
        }
        else
        {
            profiles[key] = bar;
        }
        _profiles = profiles;
        // The save is on its way before anyone hears of the change.
        var saving = Saving = SaveAsync();
        Revision++;
        OnPropertyChanged(nameof(IsCustomized));
        Changed?.Invoke(this, bar);
        return saving;
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (Key() != _shownFor)
        {
            _shownFor = Key();
            Revision++;
            OnPropertyChanged(nameof(IsCustomized));
            Changed?.Invoke(this, Current);
        }
    });

    private (IReadOnlyDictionary<string, PlayerBar> Profiles, IReadOnlyList<PlayerButton> StartHidden) Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return (new Dictionary<string, PlayerBar>(), []);
            }
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), PlayerButtonsJsonContext.Default.PlayerButtonsFile);
            var profiles = (file?.Profiles ?? new Dictionary<string, PlayerButtonsEntry?>())
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => new PlayerBar(ButtonsOf(pair.Value!.Order), ButtonsOf(pair.Value.Hidden)), StringComparer.Ordinal);
            return (profiles, ButtonsOf(file?.StartHidden));
        }
        catch (Exception ex)
        {
            // Whatever the file or the disk does, the app starts: the bar shows as it comes.
            _logger.LogWarning(ex, "The player's buttons could not be read; the bar shows as it comes");
            return (new Dictionary<string, PlayerBar>(), []);
        }
    }

    // A name this Granary does not know, such as a button an older one had, is left out.
    private static List<PlayerButton> ButtonsOf(IReadOnlyList<string?>? ids) =>
        [.. (ids ?? []).Select(id => PlayerBar.Buttons.FirstOrDefault(info => info.Id == id)).OfType<PlayerButtonInfo>().Select(info => info.Button)];

    private static List<string> IdsOf(IEnumerable<PlayerButton> buttons) => [.. buttons.Select(button => PlayerBar.InfoOf(button).Id)];

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        string temp = _filePath + ".tmp";
        try
        {
            await BeforeWrite().ConfigureAwait(false);
            var file = new PlayerButtonsFile(_profiles.ToDictionary(
                pair => pair.Key,
                pair => (PlayerButtonsEntry?)new PlayerButtonsEntry(pair.Value.HasDefaultOrder ? null : IdsOf(pair.Value.Order), IdsOf(pair.Value.Hidden)),
                StringComparer.Ordinal))
            {
                StartHidden = _start.Hidden.Count > 0 ? IdsOf(_start.Hidden) : null,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, PlayerButtonsJsonContext.Default.PlayerButtonsFile).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Never the click's to fail: the bar shows the change for this run.
            _logger.LogWarning(ex, "The player's buttons could not be saved");
            try
            {
                File.Delete(temp);
            }
            catch (Exception deleteFailure)
            {
                _logger.LogDebug(deleteFailure, "A half-written player buttons file stays behind");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// <c>player-buttons.json</c>: each profile's bar by "server id/profile id", its order only when it is not the bar's own,
/// and the buttons a profile without a bar of its own starts with hidden.
/// </summary>
internal sealed record PlayerButtonsFile(IReadOnlyDictionary<string, PlayerButtonsEntry?>? Profiles)
{
    public IReadOnlyList<string?>? StartHidden { get; init; }
}

internal sealed record PlayerButtonsEntry(IReadOnlyList<string?>? Order, IReadOnlyList<string?>? Hidden);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlayerButtonsFile))]
internal sealed partial class PlayerButtonsJsonContext : JsonSerializerContext;
