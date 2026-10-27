using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Overlays;

/// <summary>
/// The profile's card badges, as the web keeps them (<c>hooks/useOverlayPrefs.ts</c>): <c>ui.card_overlays</c> (which badges,
/// where, in which colour and style) and <c>ui.card_overlays_enabled</c> (badges at all), each the profile's own choice or,
/// without one, the server's defaults (<c>GET /api/v2/settings/overlay-config</c>). Read for every profile; a change saves at
/// once to the profile, so the web and Granary show the same badges. Cards show nothing until the choice is known, so no
/// default badges flash before the profile's.
/// </summary>
public sealed partial class CardOverlaySettings : ObservableObject
{
    public const string PrefsKey = "ui.card_overlays";
    public const string EnabledKey = "ui.card_overlays_enabled";

    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();
    private static readonly JsonElement False = JsonDocument.Parse("false").RootElement.Clone();

    private readonly ISessionManager _session;
    private readonly SettingsApi _settings;
    private readonly CatalogApi _catalog;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<CardOverlaySettings> _logger;
    private bool _following;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    // The profile whose own values were last read: a failed read again keeps them rather than the server's defaults.
    private (string? ServerId, string? ProfileId)? _readFor;
    private string? _serverFor;
    private int _loads;
    // Counts the viewer's changes: a read that began before one does not put the old values back.
    private int _edits;
    private int? _manifestRevision;
    private IReadOnlySet<string> _storedIds = new HashSet<string>();
    // The saves, one after another; a later change of the same setting carries the earlier ones.
    private Task _saves = Task.CompletedTask;
    private readonly Dictionary<string, int> _saveGenerations = new(StringComparer.Ordinal);
    private int _savesQueued;
    private bool _saveFailed;

    public CardOverlaySettings(ISessionManager session, SettingsApi settings, CatalogApi catalog, IUiDispatcher dispatcher,
        ILogger<CardOverlaySettings> logger)
    {
        _session = session;
        _settings = settings;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>The badges cards draw: null while they load and while badges are off.</summary>
    [ObservableProperty]
    public partial CardOverlayPrefs? Shown { get; private set; }

    /// <summary>The badges the settings edit: the profile's, else the server's defaults, else the built-in ones; also while off.</summary>
    [ObservableProperty]
    public partial CardOverlayPrefs Prefs { get; private set; } = CardOverlayPrefs.Default;

    [ObservableProperty]
    public partial bool IsEnabled { get; private set; } = true;

    /// <summary>The profile chose its own badges; Reset returns it to the server's defaults.</summary>
    [ObservableProperty]
    public partial bool HasOverride { get; private set; }

    /// <summary>The server stores badge choices; an older one does not, and the settings only show its defaults.</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    /// <summary>The rating sources the server shows; a rating badge of another source is not offered.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<string> ShownRatingSources { get; private set; } = new HashSet<string>();

    /// <summary>The read or save in flight, for tests.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>The settings offer the badge: the server can store it and, for a rating, shows its source.</summary>
    public bool IsOffered(OverlayDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return CardOverlayPrefs.IsSupportedBy(def.Id, _manifestRevision, _storedIds)
            && (def.RatingSource is not { } source || ShownRatingSources.Contains(source));
    }

    /// <summary>Reads the profile's badges, and reads them again for every other profile from then on.</summary>
    public Task StartAsync()
    {
        if (!_following)
        {
            _following = true;
            _session.Changed += OnSessionChanged;
        }
        return Working = LoadIfNewAsync();
    }

    /// <summary>Reads the badges again, as when the settings page opens.</summary>
    public Task RefreshAsync()
    {
        _loadedFor = null;
        return Working = LoadIfNewAsync();
    }

    /// <summary>Saves <paramref name="next"/> as the profile's badges: shown at once, put back as the server has it when refused.</summary>
    public Task SetPrefsAsync(CardOverlayPrefs next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var storable = next.ForServer(_manifestRevision, _storedIds);
        if (HasOverride && storable.SameAs(Prefs.ForServer(_manifestRevision, _storedIds)))
        {
            return Task.CompletedTask;
        }
        _edits++;
        Prefs = next;
        HasOverride = true;
        Shown = IsEnabled ? next : null;
        _storedIds = storable.Items.Keys.ToHashSet(StringComparer.Ordinal);
        return Working = QueueSave(PrefsKey, storable.ToJson());
    }

    public Task SetEnabledAsync(bool enabled)
    {
        if (enabled == IsEnabled)
        {
            return Task.CompletedTask;
        }
        _edits++;
        IsEnabled = enabled;
        HasOverride = true;
        Shown = enabled ? Prefs : null;
        return Working = QueueSave(EnabledKey, enabled ? True : False);
    }

    /// <summary>Drops the profile's choice, both of the badges and of showing them; the server's defaults apply again.</summary>
    public Task ResetAsync() => Working = ResetCoreAsync();

    private async Task ResetCoreAsync()
    {
        // After the saves on their way, or one of them would put a choice back.
        await _saves.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        foreach (string key in new[] { PrefsKey, EnabledKey })
        {
            try
            {
                await _settings.ClearValueAsync(server.BaseUri, key, SettingScopes.Profile);
            }
            catch (SiloApiException ex) when (ex.Status == 404)
            {
                // Nothing of the profile's own: it follows the server already.
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                _logger.LogWarning("Resetting the card badges failed: {Failure}", ApiFailureText.Describe(ex));
            }
        }
        _loadedFor = null;
        await LoadIfNewAsync();
    }

    // Saves one at a time, so the server keeps the last change; one for the profile acting when it was made, or none.
    private Task QueueSave(string key, JsonElement value)
    {
        int generation = _saveGenerations[key] = _saveGenerations.GetValueOrDefault(key) + 1;
        var owner = (_session.ActiveServer?.Id, _session.ProfileId);
        _savesQueued++;
        return _saves = SaveInTurnAsync(_saves, key, value, generation, owner);
    }

    private async Task SaveInTurnAsync(Task previous, string key, JsonElement value, int generation, (string?, string?) owner)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        try
        {
            bool latest = generation == _saveGenerations[key];
            if (latest && owner == (_session.ActiveServer?.Id, _session.ProfileId))
            {
                await SaveAsync(key, value);
            }
        }
        finally
        {
            if (--_savesQueued == 0 && _saveFailed)
            {
                // Refused or lost: once nothing else is on its way, show what the server has.
                _saveFailed = false;
                _loadedFor = null;
                await LoadIfNewAsync();
            }
        }
    }

    private async Task SaveAsync(string key, JsonElement value)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            await _settings.SetValueAsync(server.BaseUri, key, value, SettingScopes.Profile);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Saving the card badges failed: {Failure}", ApiFailureText.Describe(ex));
            _saveFailed = true;
        }
    }

    private Task LoadIfNewAsync()
    {
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server } || _session.ProfileId is null)
        {
            _loadedFor = null;
            Shown = null;
            return Task.CompletedTask;
        }
        if (_loadedFor == key)
        {
            return Task.CompletedTask;
        }
        // Another profile's badges stay until this one's are known: the same server mostly shows the same.
        _loadedFor = key;
        return LoadAsync(server.BaseUri, key, ++_loads);
    }

    private async Task LoadAsync(Uri server, (string? ServerId, string? ProfileId) key, int load)
    {
        int edits = _edits;
        OverlayConfig? config = null;
        EffectiveSettings? effective = null;
        bool knowsKeys = true;
        bool readFailed = false;
        int? revision = null;
        bool revisionRead = false;
        IReadOnlySet<string>? ratingSources = null;
        try
        {
            config = await _settings.GetOverlayConfigAsync(server);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Reading the server's card badges failed: {Failure}", ApiFailureText.Describe(ex));
        }
        try
        {
            effective = await _settings.GetEffectiveAsync(server, [PrefsKey, EnabledKey]);
        }
        catch (SiloApiException ex) when (ex.Status == 422)
        {
            // An older server: its settings contract does not know the badges, so only its defaults apply.
            knowsKeys = false;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Reading the profile's card badges failed: {Failure}", ApiFailureText.Describe(ex));
            readFailed = true;
        }
        try
        {
            var capabilities = await _settings.GetCapabilitiesAsync(server);
            revision = capabilities.ApiVersion == SettingsApi.ApiVersion ? capabilities.ManifestRevision : null;
            revisionRead = true;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("The server's settings revision is unknown: {Failure}", ApiFailureText.Describe(ex));
        }
        try
        {
            ratingSources = (await _catalog.GetRatingsCapabilityAsync(server)).Shown;
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // A server before ratings shows no Rotten Tomatoes scores: none are offered.
            ratingSources = new HashSet<string>();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("The server's rating sources are unknown: {Failure}", ApiFailureText.Describe(ex));
        }
        if (load != _loads || (_session.ActiveServer?.Id, _session.ProfileId) != key)
        {
            return;
        }
        ShowServer(key.ServerId, revisionRead, revision, ratingSources);
        if (readFailed)
        {
            // The next refresh, or the next profile, reads again; until then what shows stays.
            _loadedFor = null;
            if (_readFor == key)
            {
                return;
            }
            // Never read for this profile: the server's defaults stand in, and editing waits, as it would overwrite a
            // choice not seen.
            CanEdit = false;
            HasOverride = false;
            if (edits == _edits)
            {
                Show(CardOverlayPrefs.ParseText(config?.Defaults), config?.Enabled != false);
            }
            return;
        }
        _readFor = key;
        CanEdit = knowsKeys;
        if (edits != _edits)
        {
            // The viewer changed the badges while this read was on its way: theirs stand, and are on their way to the server.
            return;
        }
        var stored = effective?.Find(PrefsKey) is { ValueKind: not JsonValueKind.Null } value ? value : (JsonElement?)null;
        var prefs = stored is not null ? CardOverlayPrefs.Parse(stored) : CardOverlayPrefs.ParseText(config?.Defaults);
        bool enabled = effective?.Find(EnabledKey) is { ValueKind: JsonValueKind.True or JsonValueKind.False } choice ? choice.GetBoolean() : config?.Enabled != false;
        _storedIds = CardOverlayPrefs.StoredIds(stored);
        HasOverride = effective?.Items.Any(item => item.Key is PrefsKey or EnabledKey && item.Source == SettingScopes.Profile) == true;
        Show(prefs, enabled);
    }

    // What the server can store and which ratings it shows; a lost answer keeps what is known of the same server.
    private void ShowServer(string? serverId, bool revisionRead, int? revision, IReadOnlySet<string>? ratingSources)
    {
        if (serverId != _serverFor)
        {
            _serverFor = serverId;
            _manifestRevision = null;
            ratingSources ??= new HashSet<string>();
        }
        if (revisionRead)
        {
            _manifestRevision = revision;
        }
        if (ratingSources is not null && !ratingSources.SetEquals(ShownRatingSources))
        {
            ShownRatingSources = ratingSources;
        }
    }

    // The same badges read again are not new ones: the cards keep theirs.
    private void Show(CardOverlayPrefs prefs, bool enabled)
    {
        if (!prefs.SameAs(Prefs))
        {
            Prefs = prefs;
        }
        IsEnabled = enabled;
        Shown = enabled ? Prefs : null;
    }

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Working = LoadIfNewAsync());
}
