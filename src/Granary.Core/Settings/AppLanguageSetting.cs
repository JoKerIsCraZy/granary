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

    public AppLanguageSetting(ISessionManager session, IProfileSettings settings, SettingsApi api, AppLanguageFile file, IUiDispatcher dispatcher,
        ILogger<AppLanguageSetting> logger)
    {
        _session = session;
        _settings = settings;
        _api = api;
        _file = file;
        _dispatcher = dispatcher;
        _logger = logger;
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

    // The server and profile the last choice was saved for, while this PC's own value still outranks it.
    private WriteTarget? _clearPending;

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
        string? before = Stored;
        bool clearDevice = StoredForThisDevice;
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
                    Failure = AppLanguageFailure.NotSaved;
                }
                return;
            }
            if (Owner() != owner)
            {
                return;
            }
            if (!clearDevice)
            {
                await _file.KeepAsync(tag);
                return;
            }
            StoredForThisDevice = false;
            _clearPending = target;
            await ClearDeviceAsync(owner, target);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Clears this PC's own value again, after it failed: the profile's choice was saved, but that value outranks it here.</summary>
    public async Task RetryDeviceClearAsync()
    {
        if (IsSaving || _clearPending is not { } target || Failure != AppLanguageFailure.DeviceNotCleared)
        {
            return;
        }
        IsSaving = true;
        Failure = AppLanguageFailure.None;
        try
        {
            await ClearDeviceAsync(Owner(), target);
        }
        finally
        {
            IsSaving = false;
        }
    }

    // Goes out as the profile it was chosen for, refused unsent once another acts, so it never clears the next profile's.
    private async Task ClearDeviceAsync((string? ServerId, string? ProfileId) owner, WriteTarget target)
    {
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
            return;
        }
        if (Owner() == owner)
        {
            _clearPending = null;
            await _file.KeepAsync(Stored);
        }
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
            await _file.KeepAsync(null);
            return;
        }
        _readFor = owner;
        Stored = SettingValues.Text(setting);
        StoredForThisDevice = Stored is not null && setting.Source == SettingScopes.ProfileDevice;
        State = AppLanguageState.Ready;
        await _file.KeepAsync(Stored);
    }

    private (string? ServerId, string? ProfileId) Owner() => (_session.ActiveServer?.Id, _session.ProfileId);

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Working = LoadIfNewAsync());
}
