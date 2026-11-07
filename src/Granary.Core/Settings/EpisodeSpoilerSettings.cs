using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Settings;

/// <summary>Whether the spoiler switches can be edited.</summary>
public enum EpisodeSpoilerState
{
    /// <summary>Not read yet for the profile.</summary>
    Loading,

    /// <summary>The read failed before anything of the profile's was known: editing waits, as it would overwrite a choice not seen.</summary>
    Unavailable,

    /// <summary>The server's settings contract predates the switches (revision 17): its read refuses the keys with 422.</summary>
    Unsupported,

    Ready,
}

/// <summary>
/// The profile's spoiler switches, <c>catalog.hide_unwatched_episode_images</c> and <c>catalog.hide_unwatched_episode_overviews</c>
/// (the web's Settings › Title Pages, the apps' <c>EpisodeSpoilerPreferences</c>): read for every profile, a change applied at
/// once and saved for the profile, on every device. Every place that shows an episode follows <see cref="Shown"/>, which
/// hides nothing until the profile's choice is known, and nothing on an older server.
/// </summary>
public sealed partial class EpisodeSpoilerSettings : ObservableObject
{
    public const string ImagesKey = "catalog.hide_unwatched_episode_images";
    public const string OverviewsKey = "catalog.hide_unwatched_episode_overviews";

    private readonly ISessionManager _session;
    private readonly SettingsApi _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<EpisodeSpoilerSettings> _logger;
    private bool _following;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    // The profile whose switches were last read: a failed read again keeps them, and editing.
    private (string? ServerId, string? ProfileId)? _readFor;
    private int _loads;
    // Counts the viewer's changes: a read that began before one does not put the old values back.
    private int _edits;
    // What the server has as far as this app knows: the last read, with the saves that landed since.
    private EpisodeSpoilers _confirmed;
    // The saves, one after another, so the server keeps the last change of each switch.
    private Task _saves = Task.CompletedTask;
    private readonly Dictionary<string, int> _saveGenerations = new(StringComparer.Ordinal);
    private int _savesQueued;
    // The switches whose last save was refused or lost; a later save of the same switch that lands takes it out.
    private readonly HashSet<string> _failedKeys = new(StringComparer.Ordinal);

    public EpisodeSpoilerSettings(ISessionManager session, SettingsApi settings, IUiDispatcher dispatcher, ILogger<EpisodeSpoilerSettings> logger)
    {
        _session = session;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>What every place that shows an episode hides now.</summary>
    [ObservableProperty]
    public partial EpisodeSpoilers Shown { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial EpisodeSpoilerState State { get; private set; } = EpisodeSpoilerState.Loading;

    public bool CanEdit => State == EpisodeSpoilerState.Ready;

    /// <summary>The last change was refused or lost, and the switches went back to what the server has; the next change clears it.</summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    /// <summary>The read or save in flight, for tests.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the profile's switches, and reads them again for every other profile from then on.</summary>
    public Task StartAsync()
    {
        if (!_following)
        {
            _following = true;
            _session.Changed += OnSessionChanged;
        }
        return Working = LoadIfNewAsync();
    }

    /// <summary>Reads the switches again, as when the settings page opens: another device may have changed them.</summary>
    public Task RefreshAsync()
    {
        _loadedFor = null;
        return Working = LoadIfNewAsync();
    }

    /// <summary>Blurs unwatched episodes' stills, or stops: applied at once, put back as the server has it when refused.</summary>
    public Task SetHidesImagesAsync(bool hides) => SetAsync(ImagesKey, Shown with { HidesImages = hides });

    /// <summary>Leaves out unwatched episodes' descriptions, or stops.</summary>
    public Task SetHidesOverviewsAsync(bool hides) => SetAsync(OverviewsKey, Shown with { HidesOverviews = hides });

    private Task SetAsync(string key, EpisodeSpoilers next)
    {
        if (!CanEdit || next == Shown)
        {
            return Task.CompletedTask;
        }
        _edits++;
        SaveFailed = false;
        Shown = next;
        bool value = key == ImagesKey ? next.HidesImages : next.HidesOverviews;
        return Working = QueueSave(key, value);
    }

    // Saves one at a time, so the server keeps the last change; one for the profile acting when it was made, or none.
    private Task QueueSave(string key, bool value)
    {
        int generation = _saveGenerations[key] = _saveGenerations.GetValueOrDefault(key) + 1;
        var owner = Owner();
        _savesQueued++;
        return _saves = SaveInTurnAsync(_saves, key, value, generation, owner);
    }

    private async Task SaveInTurnAsync(Task previous, string key, bool value, int generation, (string?, string?) owner)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        try
        {
            if (generation == _saveGenerations[key] && owner == Owner())
            {
                await SaveAsync(key, value);
            }
        }
        finally
        {
            if (--_savesQueued == 0 && _failedKeys.Count > 0)
            {
                // Refused or lost: once nothing else is on its way, back to what was saved, then what the server has. This save
                // is the last one on its way, so the read does not wait for it.
                _failedKeys.Clear();
                SaveFailed = true;
                if (_readFor == Owner())
                {
                    Shown = _confirmed;
                }
                _loadedFor = null;
                await LoadIfNewAsync(Task.CompletedTask);
            }
        }
    }

    private async Task SaveAsync(string key, bool value)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            // Signed out or between profiles: the change cannot be kept.
            _failedKeys.Add(key);
            return;
        }
        var owner = Owner();
        try
        {
            await _settings.SetValueAsync(server.BaseUri, key, Json(value), SettingScopes.Profile);
            _failedKeys.Remove(key);
            if (owner == Owner() && _readFor == owner)
            {
                _confirmed = key == ImagesKey ? _confirmed with { HidesImages = value } : _confirmed with { HidesOverviews = value };
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Saving the spoiler settings failed: {Failure}", ApiFailureText.Describe(ex));
            _failedKeys.Add(key);
        }
    }

    private static JsonElement Json(bool value)
    {
        using var document = JsonDocument.Parse(value ? "true" : "false");
        return document.RootElement.Clone();
    }

    private Task LoadIfNewAsync() => LoadIfNewAsync(_saves);

    // The read waits for the saves on their way: the server's answer before one lands would put the old value back.
    private Task LoadIfNewAsync(Task saves)
    {
        var key = Owner();
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server } || _session.ProfileId is null)
        {
            // Signed out or between profiles: nothing is hidden, and the next profile is read afresh.
            _loadedFor = null;
            _readFor = null;
            Forget();
            return Task.CompletedTask;
        }
        if (_loadedFor == key)
        {
            return Task.CompletedTask;
        }
        _loadedFor = key;
        if (_readFor != key)
        {
            // Another profile's switches are not this one's: nothing is hidden until these are known, as on the web.
            Forget();
            SaveFailed = false;
        }
        return LoadAsync(server.BaseUri, key, ++_loads, saves);
    }

    private void Forget()
    {
        State = EpisodeSpoilerState.Loading;
        Shown = _confirmed = EpisodeSpoilers.Off;
    }

    private async Task LoadAsync(Uri server, (string? ServerId, string? ProfileId) key, int load, Task saves)
    {
        int edits = _edits;
        await saves.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (load != _loads)
        {
            // A later read is on its way.
            return;
        }
        EffectiveSettings? effective = null;
        var state = EpisodeSpoilerState.Ready;
        try
        {
            effective = await _settings.GetEffectiveAsync(server, [ImagesKey, OverviewsKey]);
        }
        catch (SiloApiException ex) when (ex.Status == 422)
        {
            // An older server: its settings contract does not know the switches, so nothing is hidden.
            state = EpisodeSpoilerState.Unsupported;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Reading the spoiler settings failed: {Failure}", ApiFailureText.Describe(ex));
            state = EpisodeSpoilerState.Unavailable;
        }
        if (load != _loads || Owner() != key)
        {
            return;
        }
        if (state == EpisodeSpoilerState.Unavailable)
        {
            // The next refresh, or the next profile, reads again; until then switches read before stay editable.
            _loadedFor = null;
            if (_readFor == key)
            {
                return;
            }
        }
        else
        {
            _readFor = key;
        }
        State = state;
        if (state != EpisodeSpoilerState.Ready)
        {
            _confirmed = EpisodeSpoilers.Off;
            if (edits == _edits)
            {
                Shown = EpisodeSpoilers.Off;
            }
            return;
        }
        if (edits != _edits)
        {
            // The viewer changed one while this read was on its way: theirs stands, and is on its way to the server.
            return;
        }
        Shown = _confirmed = new EpisodeSpoilers(IsOn(effective, ImagesKey), IsOn(effective, OverviewsKey));
    }

    // Anything but true reads as the default, off, as the apps read it.
    private static bool IsOn(EffectiveSettings? effective, string key) => effective?.Find(key) is { ValueKind: JsonValueKind.True };

    private (string? ServerId, string? ProfileId) Owner() => (_session.ActiveServer?.Id, _session.ProfileId);

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Working = LoadIfNewAsync());
}
