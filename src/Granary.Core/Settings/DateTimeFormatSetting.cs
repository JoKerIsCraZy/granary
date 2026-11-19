using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Settings;

/// <summary>
/// The acting profile's date and time formats, <c>ui.date_format</c> and <c>ui.time_format</c>, as the web's
/// <c>DateTimeFormatProvider</c> (hooks/useDateTimeFormat.tsx) keeps them: read for every profile, with the app language,
/// applied to every date written from then on (<see cref="DateText"/>) and kept on this PC (<see cref="DateTimeFormatFile"/>)
/// for the next start. Both are the profile's alone (no device scope). A choice applies at once and is saved for the profile;
/// one the server refuses goes back to what it has, as the web's query reconciles.
/// </summary>
public sealed partial class DateTimeFormatSetting : ObservableObject
{
    public const string DateKey = "ui.date_format";
    public const string TimeKey = "ui.time_format";

    private static readonly string[] Keys = [DateKey, TimeKey];

    private readonly ISessionManager _session;
    private readonly IProfileSettings _settings;
    private readonly DateTimeFormatFile _file;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<DateTimeFormatSetting> _logger;
    private readonly Action<DateTimePreferences> _apply;
    private bool _following;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    // The profile whose formats Preferences are.
    private (string? ServerId, string? ProfileId)? _readFor;
    private int _loads;
    // Counts the viewer's choices: a read that began before one does not put the old value back.
    private int _choices;
    // What the server has as far as this app knows: the last read, with the saves that landed since.
    private DateTimePreferences _confirmed;
    // The saves, one after another, so the server keeps the last choice.
    private Task _saves = Task.CompletedTask;
    private int _savesQueued;
    private bool _saveFailed;

    /// <param name="apply">Where the formats in use go: <see cref="DateText.Apply"/> in the app.</param>
    public DateTimeFormatSetting(ISessionManager session, IProfileSettings settings, DateTimeFormatFile file, IUiDispatcher dispatcher,
        ILogger<DateTimeFormatSetting> logger, Action<DateTimePreferences> apply)
    {
        _session = session;
        _settings = settings;
        _file = file;
        _dispatcher = dispatcher;
        _logger = logger;
        _apply = apply;
        Preferences = _confirmed = file.Kept;
    }

    /// <summary>The formats dates are written in: as kept on this PC until the profile's are read, then the profile's.</summary>
    [ObservableProperty]
    public partial DateTimePreferences Preferences { get; private set; }

    /// <summary>The profile's formats are known, so a choice can be made; until then a choice would overwrite one not seen.</summary>
    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    /// <summary>The read failed before the profile's formats were known; a refresh reads them again.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>The read or save in flight, for tests.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the profile's formats, and reads them again for every other profile from then on.</summary>
    public Task StartAsync()
    {
        if (!_following)
        {
            _following = true;
            _session.Changed += OnSessionChanged;
        }
        return Working = LoadIfNewAsync();
    }

    /// <summary>Reads the formats again, as when the settings page opens: another device may have changed them.</summary>
    public Task RefreshAsync()
    {
        _loadedFor = null;
        return Working = LoadIfNewAsync();
    }

    /// <summary>Chooses the date format, one of <see cref="DateTimePreferences.DateFormats"/>.</summary>
    public Task ChooseDateFormatAsync(string format) =>
        ChooseAsync(DateKey, format, Preferences.WithDateFormat(format), confirmed => confirmed.WithDateFormat(format));

    /// <summary>Chooses the time format, one of <see cref="DateTimePreferences.TimeFormats"/>.</summary>
    public Task ChooseTimeFormatAsync(string format) =>
        ChooseAsync(TimeKey, format, Preferences.WithTimeFormat(format), confirmed => confirmed.WithTimeFormat(format));

    private Task ChooseAsync(string key, string value, DateTimePreferences next, Func<DateTimePreferences, DateTimePreferences> confirm)
    {
        if (!IsReady || next == Preferences || _readFor != Owner() || _session.State != SessionState.Ready)
        {
            return Task.CompletedTask;
        }
        _choices++;
        Show(next);
        var owner = Owner();
        _savesQueued++;
        return Working = _saves = SaveInTurnAsync(_saves, key, value, owner, confirm);
    }

    private async Task SaveInTurnAsync(Task previous, string key, string value, (string?, string?) owner,
        Func<DateTimePreferences, DateTimePreferences> confirm)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        try
        {
            await SaveAsync(key, value, owner, confirm);
        }
        finally
        {
            if (--_savesQueued == 0)
            {
                await SettleAsync();
            }
        }
    }

    // Once nothing else is on its way: a refused or lost choice goes back to what the server has, which is then read again;
    // otherwise what was chosen is kept for the next start.
    private async Task SettleAsync()
    {
        if (!_saveFailed)
        {
            await _file.KeepAsync(Preferences);
            return;
        }
        _saveFailed = false;
        if (_readFor == Owner())
        {
            Show(_confirmed);
        }
        _loadedFor = null;
        await LoadIfNewAsync();
    }

    private async Task SaveAsync(string key, string value, (string?, string?) owner, Func<DateTimePreferences, DateTimePreferences> confirm)
    {
        if (_session.State != SessionState.Ready || owner != Owner())
        {
            // Signed out or another profile meanwhile: the choice cannot be kept.
            _saveFailed = true;
            return;
        }
        try
        {
            await _settings.SaveAsync(key, SettingValues.Json(value), clearDevice: false, CancellationToken.None);
            if (owner == Owner() && _readFor == owner)
            {
                _confirmed = confirm(_confirmed);
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("Saving the date and time formats failed: {Failure}", ApiFailureText.Describe(ex));
            _saveFailed = true;
        }
    }

    // The read waits for the choices on their way: the server's answer before one lands would put the old value back.
    private Task LoadIfNewAsync()
    {
        var owner = Owner();
        if (_session is not { State: SessionState.Ready, ActiveServer: not null } || _session.ProfileId is null)
        {
            // Signed out or between profiles: the formats in use stay, as the web keeps its own until the next are read.
            _loadedFor = null;
            _readFor = null;
            _loads++;
            IsReady = false;
            LoadFailed = false;
            return Task.CompletedTask;
        }
        if (_loadedFor == owner)
        {
            return Task.CompletedTask;
        }
        _loadedFor = owner;
        if (_readFor != owner)
        {
            IsReady = false;
            LoadFailed = false;
        }
        return LoadAsync(owner, ++_loads, _saves);
    }

    private async Task LoadAsync((string? ServerId, string? ProfileId) owner, int load, Task saves)
    {
        await saves.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        int choices = _choices;
        IReadOnlyDictionary<string, EffectiveSetting>? values = null;
        try
        {
            values = await _settings.ReadAsync(Keys, CancellationToken.None);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("Reading the date and time formats failed: {Failure}", ApiFailureText.Describe(ex));
        }
        if (load != _loads || Owner() != owner)
        {
            // A later read, or another profile, is on its way.
            return;
        }
        if (values is null)
        {
            // The next refresh, or the next profile, reads again; until then the formats in use stay.
            _loadedFor = null;
            if (_readFor != owner)
            {
                LoadFailed = true;
            }
            return;
        }
        // An unset key, or one a server does not know, is auto, the contract's default.
        _confirmed = DateTimePreferences.Parse(
            SettingValues.Text(values.GetValueOrDefault(DateKey)), SettingValues.Text(values.GetValueOrDefault(TimeKey)));
        _readFor = owner;
        LoadFailed = false;
        IsReady = true;
        if (choices != _choices || _savesQueued > 0)
        {
            // The viewer chose meanwhile: that choice is newer than what the server answered.
            return;
        }
        Show(_confirmed);
        await _file.KeepAsync(_confirmed);
    }

    private void Show(DateTimePreferences preferences)
    {
        Preferences = preferences;
        _apply(preferences);
    }

    private (string? ServerId, string? ProfileId) Owner() => (_session.ActiveServer?.Id, _session.ProfileId);

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Working = LoadIfNewAsync());
}
