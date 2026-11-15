using System.Text.Json;
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

/// <summary>Whether the profile's app language is known.</summary>
public enum AppLanguageState
{
    /// <summary>Not read yet for the profile, or no profile acts.</summary>
    Loading,

    /// <summary>The read failed: choosing waits, as it would overwrite a choice not seen.</summary>
    Unavailable,

    /// <summary>The server predates the setting (settings contract revision 28): there is nowhere to keep a choice.</summary>
    Unsupported,

    Ready,
}

/// <summary>What went wrong with the last choice, in the web's words for each (<c>pages/settings/AccessibilitySettings.tsx</c>).</summary>
public enum AppLanguageFailure
{
    None,

    /// <summary>The profile's value was not saved: the choice went back.</summary>
    NotSaved,

    /// <summary>The profile's value was saved, but this PC's own still outranks it here; clearing it can be tried again.</summary>
    DeviceNotCleared,
}

/// <summary>
/// The acting profile's app language, <c>ui.app_language</c> (silo-server docs/settings-api.md, "App language"): read for
/// every profile, as the web's <c>LocaleProvider</c> reads it, and kept on this PC (<see cref="AppLanguageFile"/>), which the
/// next start follows. Granary loads its texts once, at start, so what is read or chosen here never changes the language on
/// screen. A choice (<see cref="ChooseAsync"/>) is written as the web's Settings → Accessibility and the apps write it: the
/// profile's value, which follows the person to every device, then this PC's own value cleared, so it applies here too.
/// </summary>
public sealed partial class AppLanguageSetting : ObservableObject
{
    public const string Key = "ui.app_language";

    private static readonly string[] Keys = [Key];

    private readonly ISessionManager _session;
    private readonly IProfileSettings _settings;
    private readonly SettingsApi _api;
    private readonly AppLanguageFile _file;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<AppLanguageSetting> _logger;
    private bool _following;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    // The profile whose language Stored is.
    private (string? ServerId, string? ProfileId)? _readFor;
    private int _loads;
    // Counts the choices and their retries, and whose the last was: a read for that profile that began before one, or
    // while one saved, or ends while one saves, does not put the old language back.
    private int _choices;
    private (string? ServerId, string? ProfileId)? _choiceFor;

    public AppLanguageSetting(ISessionManager session, IProfileSettings settings, SettingsApi api, AppLanguageFile file, IUiDispatcher dispatcher,
        ILogger<AppLanguageSetting> logger)
    {
        _session = session;
        _settings = settings;
        _api = api;
        _file = file;
        _dispatcher = dispatcher;
        _logger = logger;
        KeptForNextStart = file.Kept;
    }

    [ObservableProperty]
    public partial AppLanguageState State { get; private set; } = AppLanguageState.Loading;

    /// <summary>The profile's language on this PC, as stored (it may be one Granary does not offer); null follows the device.</summary>
    [ObservableProperty]
    public partial string? Stored { get; private set; }

    /// <summary>Whether the value came from this PC's own row (<c>profile_device</c>), which outranks the profile's.</summary>
    public bool StoredForThisDevice { get; private set; }

    /// <summary>A choice is on its way; another waits until it lands, as on the web.</summary>
    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    /// <summary>What went wrong with the last choice; the next choice clears it.</summary>
    [ObservableProperty]
    public partial AppLanguageFailure Failure { get; private set; }

    // The last choice, while this PC's own value still outranks it here: whose it is, where it goes, and the language.
    private PendingClear? _clearPending;

    /// <summary>What the next start follows, as kept on this PC (<see cref="AppLanguageFile.Kept"/>).</summary>
    [ObservableProperty]
    public partial string? KeptForNextStart { get; private set; }

    /// <summary>The read in flight, for tests.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the profile's language, and reads it again for every other profile from then on.</summary>
    public Task StartAsync()
    {
        if (!_following)
        {
            _following = true;
            _session.Changed += OnSessionChanged;
        }
        return Working = LoadIfNewAsync();
    }

    /// <summary>Reads the language again, as when the settings page opens: another device may have changed it.</summary>
    public Task RefreshAsync()
    {
        _loadedFor = null;
        return Working = LoadIfNewAsync();
    }

    /// <summary>
    /// Saves <paramref name="tag"/> as the profile's app language, null to match the device; shown at once, back again if the
    /// server refuses it. One choice at a time: another while one saves is not taken.
    /// </summary>
    public async Task ChooseAsync(string? tag)
    {
        if (State != AppLanguageState.Ready || IsSaving || tag == Stored
            || _session is not { ActiveServer: { } server, ProfileId: { } profileId })
        {
            return;
        }
        var owner = Owner();
        var target = new WriteTarget(server.BaseUri, profileId);
        _choices++;
        _choiceFor = owner;
        string? before = Stored;
        bool clearDevice = StoredForThisDevice;
        // A choice before whose clearing of this PC's own value failed: a refused choice leaves its Try again as it was.
        var pendingBefore = _clearPending;
        IsSaving = true;
        Failure = AppLanguageFailure.None;
        _clearPending = null;
        Stored = tag;
        try
        {
            try
            {
                await _api.SetValueAsProfileAsync(target.Server, Key, Json(tag), SettingScopes.Profile, target.ProfileId);
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is ProfileChangedException or InvalidOperationException)
            {
                _logger.LogWarning("Saving the app language failed: {Failure}", ApiFailureText.Describe(ex));
                if (Owner() == owner)
                {
                    Stored = before;
                    _clearPending = pendingBefore;
                    Failure = pendingBefore is null ? AppLanguageFailure.NotSaved : AppLanguageFailure.DeviceNotCleared;
                }
                return;
            }
            if (Owner() != owner)
            {
                return;
            }
            if (!clearDevice)
            {
                await KeepAsync(tag);
                return;
            }
            _clearPending = new PendingClear(owner, target, tag);
            await ClearDeviceAsync(_clearPending.Value);
        }
        finally
        {
            IsSaving = false;
            ReadIfDropped();
        }
    }

    /// <summary>
    /// Clears this PC's own value again, after it failed: the profile's choice was saved, but that value outranks it here.
    /// Once cleared, the profile's language is read again, as another device may have chosen one since.
    /// </summary>
    public async Task RetryDeviceClearAsync()
    {
        if (IsSaving || _clearPending is not { } pending || pending.Owner != Owner() || Failure != AppLanguageFailure.DeviceNotCleared)
        {
            return;
        }
        _choices++;
        _choiceFor = pending.Owner;
        IsSaving = true;
        Failure = AppLanguageFailure.None;
        try
        {
            if (await ClearDeviceAsync(pending))
            {
                // The chosen language stands until the read says what the profile has now.
                _loadedFor = null;
            }
        }
        finally
        {
            IsSaving = false;
            ReadIfDropped();
        }
    }

    // Goes out as the profile it was chosen for, refused unsent once another acts, so it never clears the next profile's.
    // Once cleared, the choice applies here: it shows, and the next start follows it. True when it was cleared for the
    // profile that still acts.
    private async Task<bool> ClearDeviceAsync(PendingClear pending)
    {
        var (owner, target, tag) = pending;
        try
        {
            await _api.ClearValueAsProfileAsync(target.Server, Key, SettingScopes.ProfileDevice, target.ProfileId);
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Nothing to clear is what this asked for.
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is ProfileChangedException or InvalidOperationException)
        {
            _logger.LogWarning("Clearing this PC's app language failed: {Failure}", ApiFailureText.Describe(ex));
            if (Owner() == owner)
            {
                Failure = AppLanguageFailure.DeviceNotCleared;
            }
            return false;
        }
        if (Owner() != owner)
        {
            return false;
        }
        _clearPending = null;
        Stored = tag;
        StoredForThisDevice = false;
        await KeepAsync(tag);
        return true;
    }

    // A read dropped while a choice saved left the profile's state to that choice; read again once it is done, so the
    // page and the next start follow what the server has.
    private void ReadIfDropped()
    {
        if (_loadedFor is null)
        {
            Working = LoadIfNewAsync();
        }
    }

    private async Task KeepAsync(string? tag)
    {
        await _file.KeepAsync(tag);
        KeptForNextStart = _file.Kept;
    }

    // Match device writes null, as the web's does, rather than clearing the profile's value.
    private static JsonElement Json(string? tag)
    {
        if (tag is not null)
        {
            return SettingValues.Json(tag);
        }
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    // The server and profile a choice was made for.
    private readonly record struct WriteTarget(Uri Server, string ProfileId);

    // A choice saved for the profile whose clearing of this PC's own value is still to land.
    private readonly record struct PendingClear((string? ServerId, string? ProfileId) Owner, WriteTarget Target, string? Tag);

    private Task LoadIfNewAsync()
    {
        var owner = Owner();
        if (_session is not { State: SessionState.Ready, ActiveServer: not null } || _session.ProfileId is null)
        {
            // Signed out or between profiles: nothing is known, and the language kept for the next start stays.
            _loadedFor = null;
            _readFor = null;
            _loads++;
            Forget();
            return Task.CompletedTask;
        }
        if (_loadedFor == owner)
        {
            return Task.CompletedTask;
        }
        if (_readFor != owner)
        {
            // Another profile's language is not this one's.
            Forget();
        }
        _loadedFor = owner;
        return LoadAsync(owner, ++_loads);
    }

    private void Forget()
    {
        State = AppLanguageState.Loading;
        Stored = null;
        StoredForThisDevice = false;
        Failure = AppLanguageFailure.None;
        _clearPending = null;
    }

    private async Task LoadAsync((string? ServerId, string? ProfileId) owner, int load)
    {
        int choices = _choices;
        bool savingAtStart = IsSaving;
        IReadOnlyDictionary<string, EffectiveSetting>? values = null;
        try
        {
            // An older server refuses the key; ProfileSettings then leaves it out.
            values = await _settings.ReadAsync(Keys, CancellationToken.None);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is InvalidOperationException)
        {
            _logger.LogWarning("Reading the app language failed: {Failure}", ApiFailureText.Describe(ex));
        }
        if (load != _loads || Owner() != owner)
        {
            // A later read, or another profile, is on its way.
            return;
        }
        if (_choiceFor == owner && (choices != _choices || savingAtStart || IsSaving))
        {
            // The viewer chose meanwhile: that choice is newer than what the server answered. Read again once it is done,
            // or now when it already is.
            _loadedFor = null;
            if (!IsSaving)
            {
                Working = LoadIfNewAsync();
            }
            return;
        }
        if (values is null)
        {
            // The next refresh, or the next profile, reads again; the next start keeps the language it would have had.
            _loadedFor = null;
            if (_readFor != owner)
            {
                State = AppLanguageState.Unavailable;
            }
            return;
        }
        if (!values.TryGetValue(Key, out var setting))
        {
            // Nowhere to keep a language: the profile follows the device, as on a server that has none stored.
            _readFor = owner;
            State = AppLanguageState.Unsupported;
            Stored = null;
            StoredForThisDevice = false;
            await KeepAsync(null);
            return;
        }
        _readFor = owner;
        Stored = SettingValues.Text(setting);
        // This PC's own row outranks the profile's, a null one too (it follows the device here): a choice clears it.
        StoredForThisDevice = setting.Source == SettingScopes.ProfileDevice;
        State = AppLanguageState.Ready;
        await KeepAsync(Stored);
    }

    private (string? ServerId, string? ProfileId) Owner() => (_session.ActiveServer?.Id, _session.ProfileId);

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Working = LoadIfNewAsync());
}
