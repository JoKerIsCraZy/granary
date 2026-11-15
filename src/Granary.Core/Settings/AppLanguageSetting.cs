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

/// <summary>
/// The acting profile's app language, <c>ui.app_language</c> (silo-server docs/settings-api.md, "App language"): read for
/// every profile, as the web's <c>LocaleProvider</c> reads it, and kept on this PC (<see cref="AppLanguageFile"/>), which the
/// next start follows. Granary loads its texts once, at start, so what is read here never changes the language on screen.
/// </summary>
public sealed partial class AppLanguageSetting : ObservableObject
{
    public const string Key = "ui.app_language";

    private static readonly string[] Keys = [Key];

    private readonly ISessionManager _session;
    private readonly IProfileSettings _settings;
    private readonly AppLanguageFile _file;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<AppLanguageSetting> _logger;
    private bool _following;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    // The profile whose language Stored is.
    private (string? ServerId, string? ProfileId)? _readFor;
    private int _loads;

    public AppLanguageSetting(ISessionManager session, IProfileSettings settings, AppLanguageFile file, IUiDispatcher dispatcher,
        ILogger<AppLanguageSetting> logger)
    {
        _session = session;
        _settings = settings;
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
