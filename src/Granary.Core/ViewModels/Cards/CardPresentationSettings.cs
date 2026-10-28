using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Cards;

/// <summary>Whether the poster settings can be edited, as the web's blockers on Navigation &amp; Cards.</summary>
public enum CardPresentationState
{
    /// <summary>Not read yet for the profile.</summary>
    Loading,

    /// <summary>The read failed before anything of the profile's was known: editing waits, as it would overwrite a choice not seen.</summary>
    Unavailable,

    /// <summary>The server's settings contract predates the setting (its read refuses the key with 422).</summary>
    Unsupported,

    Ready,
}

/// <summary>
/// The profile's poster size and caption (<c>ui.card_presentation</c>), as the web keeps them for its browsers
/// (<c>pages/settings/InterfaceSettings.tsx</c>), here for the desktop apps: read for every profile, a change shown at once and
/// saved for the desktop client family (<see cref="SettingScopes.ProfileClient"/>). Cards follow <see cref="Shown"/>, the
/// default until the profile's is known.
/// </summary>
public sealed partial class CardPresentationSettings : ObservableObject
{
    public const string Key = "ui.card_presentation";

    private readonly ISessionManager _session;
    private readonly SettingsApi _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<CardPresentationSettings> _logger;
    private bool _following;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    // The profile whose value was last read: a failed read again keeps it, and editing.
    private (string? ServerId, string? ProfileId)? _readFor;
    private int _loads;
    // Counts the viewer's changes: a read that began before one does not put the old value back.
    private int _edits;
    // The saves, one after another; a later change carries the earlier ones.
    private Task _saves = Task.CompletedTask;
    private int _saveGeneration;
    private int _savesQueued;
    private bool _saveFailed;

    public CardPresentationSettings(ISessionManager session, SettingsApi settings, IUiDispatcher dispatcher,
        ILogger<CardPresentationSettings> logger)
    {
        _session = session;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>The size and caption cards show: this PC's, the desktop apps', the profile's, or the default.</summary>
    [ObservableProperty]
    public partial CardPresentation Shown { get; private set; } = CardPresentation.Default;

    /// <summary>The desktop apps have a choice of their own; Reset drops it.</summary>
    [ObservableProperty]
    public partial bool HasOverride { get; private set; }

    /// <summary>This PC has a value of its own, which outranks the desktop apps' choice, so a change here would not show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool HasDeviceOverride { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial CardPresentationState State { get; private set; } = CardPresentationState.Loading;

    public bool CanEdit => State == CardPresentationState.Ready && !HasDeviceOverride;

    /// <summary>The read or save in flight, for tests.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the profile's choice, and reads it again for every other profile from then on.</summary>
    public Task StartAsync()
    {
        if (!_following)
        {
            _following = true;
            _session.Changed += OnSessionChanged;
        }
        return Working = LoadIfNewAsync();
    }

    /// <summary>Reads the choice again, as when the settings page opens.</summary>
    public Task RefreshAsync()
    {
        _loadedFor = null;
        return Working = LoadIfNewAsync();
    }

    /// <summary>Saves <paramref name="next"/> for the desktop apps: shown at once, put back as the server has it when refused.</summary>
    public Task SetAsync(CardPresentation next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (!CanEdit || next == Shown)
        {
            return Task.CompletedTask;
        }
        _edits++;
        Shown = next;
        HasOverride = true;
        return Working = QueueSave(next);
    }

    /// <summary>Drops the desktop apps' choice; the profile's, or the default, applies again.</summary>
    public Task ResetAsync() => Working = ClearAsync(SettingScopes.ProfileClient);

    /// <summary>Drops this PC's own value, so it shows the desktop apps' choice as the other computers do.</summary>
    public Task FollowOtherComputersAsync() => Working = ClearAsync(SettingScopes.ProfileDevice);

    private async Task ClearAsync(string scope)
    {
        // After the saves on their way, or one of them would put a choice back.
        await _saves.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            await _settings.ClearValueAsync(server.BaseUri, Key, scope);
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Nothing there: the next value applies already.
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Resetting the poster settings failed: {Failure}", ApiFailureText.Describe(ex));
        }
        _loadedFor = null;
        await LoadIfNewAsync();
    }

    // Saves one at a time, so the server keeps the last change; one for the profile acting when it was made, or none.
    private Task QueueSave(CardPresentation value)
    {
        int generation = ++_saveGeneration;
        var owner = (_session.ActiveServer?.Id, _session.ProfileId);
        _savesQueued++;
        return _saves = SaveInTurnAsync(_saves, value, generation, owner);
    }

    private async Task SaveInTurnAsync(Task previous, CardPresentation value, int generation, (string?, string?) owner)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        try
        {
            if (generation == _saveGeneration && owner == (_session.ActiveServer?.Id, _session.ProfileId))
            {
                await SaveAsync(value);
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

    private async Task SaveAsync(CardPresentation value)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        try
        {
            await _settings.SetValueAsync(server.BaseUri, Key, value.ToJson(), SettingScopes.ProfileClient);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Saving the poster settings failed: {Failure}", ApiFailureText.Describe(ex));
            _saveFailed = true;
        }
    }

    private Task LoadIfNewAsync()
    {
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server } || _session.ProfileId is null)
        {
            _loadedFor = null;
            return Task.CompletedTask;
        }
        if (_loadedFor == key)
        {
            return Task.CompletedTask;
        }
        // Another profile's sizes stay until this one's are known; the page waits for them.
        _loadedFor = key;
        if (_readFor != key)
        {
            State = CardPresentationState.Loading;
        }
        return LoadAsync(server.BaseUri, key, ++_loads);
    }

    private async Task LoadAsync(Uri server, (string? ServerId, string? ProfileId) key, int load)
    {
        int edits = _edits;
        EffectiveSetting? item = null;
        var state = CardPresentationState.Ready;
        try
        {
            var effective = await _settings.GetEffectiveAsync(server, [Key]);
            item = effective.Items.FirstOrDefault(i => i.Key == Key);
        }
        catch (SiloApiException ex) when (ex.Status == 422)
        {
            // An older server: its settings contract does not know the setting, so cards keep the default.
            state = CardPresentationState.Unsupported;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Reading the poster settings failed: {Failure}", ApiFailureText.Describe(ex));
            state = CardPresentationState.Unavailable;
        }
        if (load != _loads || (_session.ActiveServer?.Id, _session.ProfileId) != key)
        {
            return;
        }
        if (state == CardPresentationState.Unavailable)
        {
            // The next refresh, or the next profile, reads again; until then a value read before stays editable.
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
        if (state != CardPresentationState.Ready)
        {
            HasOverride = false;
            HasDeviceOverride = false;
            if (edits == _edits)
            {
                Shown = CardPresentation.Default;
            }
            return;
        }
        if (edits != _edits)
        {
            // The viewer changed it while this read was on its way: theirs stands, and is on its way to the server.
            return;
        }
        Shown = CardPresentation.Parse(item?.Value);
        HasOverride = item?.Source == SettingScopes.ProfileClient;
        HasDeviceOverride = item?.Source == SettingScopes.ProfileDevice;
    }

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Working = LoadIfNewAsync());
}
