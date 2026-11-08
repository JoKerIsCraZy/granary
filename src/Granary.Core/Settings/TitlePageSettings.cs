using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Settings;

/// <summary>Whether the Title Pages settings can be edited.</summary>
public enum TitlePageState
{
    /// <summary>Not read yet for the profile.</summary>
    Loading,

    /// <summary>The read failed before anything of the profile's was known: editing waits, as it would overwrite a choice not seen.</summary>
    Unavailable,

    Ready,
}

/// <summary>
/// The profile's Title Pages settings beside the spoiler switches (the web's <c>pages/settings/TitlePagesSettings.tsx</c>):
/// read for every profile, a change applied at once and saved in the order it was made. Title pages follow
/// <see cref="Shown"/>, which holds every default until the profile's choice is known, never the previous profile's.
/// </summary>
public sealed partial class TitlePageSettings : ObservableObject
{
    public const string AdvisoryAgeKey = "catalog.show_advisory_age";

    private static readonly string[] Keys = [AdvisoryAgeKey];

    private readonly ISessionManager _session;
    private readonly IProfileSettings _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<TitlePageSettings> _logger;
    private bool _following;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    // The profile whose settings were last read: a failed read again keeps them, and editing.
    private (string? ServerId, string? ProfileId)? _readFor;
    private int _loads;
    // Counts the viewer's changes: a read that began before one does not put the old value back.
    private int _edits;
    // What the server has as far as this app knows: the last read, with the saves that landed since.
    private TitlePagePrefs _confirmed = TitlePagePrefs.Unknown;
    // The saves, one after another, so the server keeps the last change.
    private Task _saves = Task.CompletedTask;
    private int _savesQueued;
    // The settings whose last save was refused or lost; a later save of the same one that lands takes it out.
    private readonly HashSet<string> _failedKeys = new(StringComparer.Ordinal);

    public TitlePageSettings(ISessionManager session, IProfileSettings settings, IUiDispatcher dispatcher, ILogger<TitlePageSettings> logger)
    {
        _session = session;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>What title pages show now.</summary>
    [ObservableProperty]
    public partial TitlePagePrefs Shown { get; private set; } = TitlePagePrefs.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial TitlePageState State { get; private set; } = TitlePageState.Loading;

    public bool CanEdit => State == TitlePageState.Ready;

    /// <summary>The last change was refused or lost, and went back to what the server has; the next change clears it.</summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    /// <summary>The read or save in flight, for tests.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the profile's settings, and reads them again for every other profile from then on.</summary>
    public Task StartAsync()
    {
        if (!_following)
        {
            _following = true;
            _session.Changed += OnSessionChanged;
        }
        return Working = LoadIfNewAsync(_saves);
    }

    /// <summary>Reads the settings again, as when the settings page opens: another device may have changed them.</summary>
    public Task RefreshAsync()
    {
        _loadedFor = null;
        return Working = LoadIfNewAsync(_saves);
    }

    /// <summary>"Show advisory age", the profile's alone.</summary>
    public Task SetShowsAdvisoryAgeAsync(bool shows) =>
        ChangeAsync(AdvisoryAgeKey, Shown with { ShowsAdvisoryAge = shows }, () => SaveForProfileAsync(AdvisoryAgeKey, shows, clearDevice: false),
            confirmed => confirmed with { ShowsAdvisoryAge = shows });

    // Applies next at once and saves it in turn, for the profile acting when it was made; a refused or lost save puts back
    // what the server has.
    private Task ChangeAsync(string key, TitlePagePrefs next, Func<Task> save, Func<TitlePagePrefs, TitlePagePrefs> confirm)
    {
        if (!CanEdit || !Shown.Offered.Contains(key) || next == Shown)
        {
            return Task.CompletedTask;
        }
        _edits++;
        SaveFailed = false;
        Shown = next;
        var owner = Owner();
        _savesQueued++;
        return Working = _saves = SaveInTurnAsync(_saves, key, owner, save, confirm);
    }

    private async Task SaveInTurnAsync(Task previous, string key, (string?, string?) owner, Func<Task> save, Func<TitlePagePrefs, TitlePagePrefs> confirm)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        try
        {
            await SaveAsync(key, owner, save, confirm);
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

    private async Task SaveAsync(string key, (string?, string?) owner, Func<Task> save, Func<TitlePagePrefs, TitlePagePrefs> confirm)
    {
        if (_session.State != SessionState.Ready || owner != Owner())
        {
            // Signed out or another profile meanwhile: the change cannot be kept.
            _failedKeys.Add(key);
            return;
        }
        try
        {
            await save();
            _failedKeys.Remove(key);
            if (owner == Owner() && _readFor == owner)
            {
                _confirmed = confirm(_confirmed);
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("Saving a title page setting failed: {Failure}", ApiFailureText.Describe(ex));
            _failedKeys.Add(key);
        }
    }

    // As the web's useProfileDefaultWriter: the profile's value, and this device's cleared when it would shadow it.
    private Task SaveForProfileAsync(string key, bool value, bool clearDevice) =>
        _settings.SaveAsync(key, Json(value), clearDevice, CancellationToken.None);

    private static JsonElement Json(bool value)
    {
        using var document = JsonDocument.Parse(value ? "true" : "false");
        return document.RootElement.Clone();
    }

    // The read waits for the saves on their way: the server's answer before one lands would put the old value back.
    private Task LoadIfNewAsync(Task saves)
    {
        var key = Owner();
        if (_session is not { State: SessionState.Ready, ActiveServer: not null } || _session.ProfileId is null)
        {
            // Signed out or between profiles: nothing shows, and the next profile is read afresh.
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
            // Another profile's settings are not this one's: the defaults until these are known.
            Forget();
            SaveFailed = false;
        }
        return LoadAsync(key, ++_loads, saves);
    }

    private void Forget()
    {
        State = TitlePageState.Loading;
        Shown = _confirmed = TitlePagePrefs.Unknown;
    }

    private async Task LoadAsync((string? ServerId, string? ProfileId) owner, int load, Task saves)
    {
        int edits = _edits;
        await saves.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (load != _loads)
        {
            // A later read is on its way.
            return;
        }
        IReadOnlyDictionary<string, EffectiveSetting>? values = null;
        try
        {
            // An older server refuses keys it does not know: those are left out, and offered no switch.
            values = await _settings.ReadAsync(Keys, CancellationToken.None);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("Reading the title page settings failed: {Failure}", ApiFailureText.Describe(ex));
        }
        if (load != _loads || Owner() != owner)
        {
            return;
        }
        if (values is null)
        {
            // The next refresh, or the next profile, reads again; until then settings read before stay, and editable.
            _loadedFor = null;
            if (_readFor != owner)
            {
                State = TitlePageState.Unavailable;
            }
            return;
        }
        _readFor = owner;
        State = TitlePageState.Ready;
        _confirmed = TitlePagePrefs.From(values);
        if (edits == _edits)
        {
            Shown = _confirmed;
        }
    }

    private (string? ServerId, string? ProfileId) Owner() => (_session.ActiveServer?.Id, _session.ProfileId);

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Working = LoadIfNewAsync(_saves));
}
