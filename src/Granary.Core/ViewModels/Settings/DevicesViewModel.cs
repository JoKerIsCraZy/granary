using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Profiles;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Devices: where the account is signed in, with Sign out (the web's Signed-in sessions), and the devices the
/// profile watches on, with Rename, Use profile settings and Forget (the web's Your Devices, without its per-device settings).
/// The sign-ins belong to the whole account, so, as on the web, only the household's primary profile or an administrator
/// without a profile sees them. Every action acts on the server and profile its lists were read for, or not at all.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject, IDisposable
{
    // As the web's device list groups them, by how recently each was used.
    private static readonly string[] GroupTitles = ["Using now", "This week", "Earlier"];

    private readonly ISessionManager _session;
    private readonly DevicesApi _devices;
    private readonly SettingsApi _settings;
    private readonly ProfilesApi _profiles;
    private readonly ClientIdentity _identity;
    private readonly IConfirmation _confirmation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<DevicesViewModel> _logger;
    private string? _nextCursor;
    private bool _devicesRead;
    // The server entry and profile the lists were read for; null before the first read.
    private (string Server, string? Profile)? _readFor;
    // Counts the reads, so an older one that answers late does not overwrite a newer one's lists.
    private int _load;

    public DevicesViewModel(ISessionManager session, DevicesApi devices, SettingsApi settings, ProfilesApi profiles, ClientIdentity identity,
        IConfirmation confirmation, IUiDispatcher dispatcher, TimeProvider time, ILogger<DevicesViewModel> logger)
    {
        _session = session;
        _devices = devices;
        _settings = settings;
        _profiles = profiles;
        _identity = identity;
        _confirmation = confirmation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        OtherSessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoOtherSessions));
        DeviceGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoDevices));
        _session.Changed += OnSessionChanged;
    }

    /// <summary>The account's sign-ins show: the primary profile, or an administrator without a profile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSessionList), nameof(HasNoOtherSessions))]
    public partial bool ShowsSessions { get; private set; }

    /// <summary>"Loading sessions…", a failure, or that the server has no session management; empty once the list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessionsMessage), nameof(ShowsSessionList), nameof(HasNoOtherSessions))]
    public partial string SessionsMessage { get; private set; } = "";

    public bool HasSessionsMessage => SessionsMessage.Length > 0;

    /// <summary>The sign-ins were read: their lists show.</summary>
    public bool ShowsSessionList => ShowsSessions && !HasSessionsMessage;

    /// <summary>The list could not be read: Retry reads it again.</summary>
    [ObservableProperty]
    public partial bool CanRetrySessions { get; private set; }

    /// <summary>Granary's own sign-in; null until read, or when the account has none (an API key).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentSession))]
    public partial SignedInSessionRow? CurrentSession { get; private set; }

    public bool HasCurrentSession => CurrentSession is not null;

    public ObservableCollection<SignedInSessionRow> OtherSessions { get; } = [];

    /// <summary>"No other signed-in sessions." once the list was read.</summary>
    public bool HasNoOtherSessions => ShowsSessionList && OtherSessions.Count == 0;

    [ObservableProperty]
    public partial bool CanLoadMoreSessions { get; private set; }

    public ObservableCollection<UserDeviceGroup> DeviceGroups { get; } = [];

    /// <summary>A failure to read the devices; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevicesMessage), nameof(HasNoDevices))]
    public partial string DevicesMessage { get; private set; } = "";

    public bool HasDevicesMessage => DevicesMessage.Length > 0;

    /// <summary>"No devices yet." once the list was read and held none.</summary>
    public bool HasNoDevices => _devicesRead && !HasDevicesMessage && DeviceGroups.Count == 0;

    /// <summary>The server can give a device a name of the profile's own.</summary>
    [ObservableProperty]
    public partial bool CanRename { get; private set; }

    /// <summary>What the last action did: "Session signed out", "Couldn't forget", …</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>Reads the sign-ins and the devices, for the server and profile in use now.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        int load = ++_load;
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _readFor = null;
            ClearSessions();
            ClearDevices();
            return;
        }
        var readFor = (server.Id, _session.ProfileId);
        if (_readFor != readFor)
        {
            // Another server's or profile's devices are not this one's to act on.
            ClearDevices();
        }
        _readFor = readFor;
        await Task.WhenAll(LoadSessionsAsync(server.BaseUri, load, cancellationToken), LoadDevicesAsync(server.BaseUri, load, cancellationToken));
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;

    /// <summary>Reads both lists again, as the web's Refresh.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    // Another server or profile while the page is kept: the lists are the old one's, so they are read again.
    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        if (_readFor is not null && !ReadForNow())
        {
            _ = ReloadForNowAsync();
        }
    });

    private async Task ReloadForNowAsync()
    {
        try
        {
            ClearSessions();
            ClearDevices();
            await LoadAsync();
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("The devices page could not be read again: {Failure}", ApiFailureText.Describe(ex));
        }
    }

    // The server and profile in use are the ones the lists were read for.
    private bool ReadForNow() =>
        _readFor is { } read && _session is { State: SessionState.Ready, ActiveServer: { } server } && server.Id == read.Server
        && _session.ProfileId == read.Profile;

    private async Task LoadSessionsAsync(Uri server, int load, CancellationToken cancellationToken)
    {
        // Nothing of an earlier read stays to act on while this one runs, or after it fails.
        ClearSessions();
        bool? sees = await SeesSessionsAsync(server, cancellationToken);
        if (load != _load)
        {
            return;
        }
        ShowsSessions = sees != false;
        if (sees is null)
        {
            SessionsMessage = "Couldn't load sessions. Try again.";
            CanRetrySessions = true;
            return;
        }
        if (sees == false)
        {
            return;
        }
        SessionsMessage = "Loading sessions…";
        try
        {
            var capabilities = await _devices.GetSessionCapabilitiesAsync(server, cancellationToken);
            if (load != _load)
            {
                return;
            }
            if (!capabilities.Available)
            {
                SessionsMessage = "Signed-in session management is unavailable on this server.";
                return;
            }
            var page = await _devices.ListSessionsAsync(server, null, cancellationToken);
            if (load != _load)
            {
                return;
            }
            _nextCursor = NextCursor(page);
            CanLoadMoreSessions = _nextCursor is not null;
            var current = page.CurrentSession ?? page.Items.FirstOrDefault(s => s.Current);
            CurrentSession = current is null ? null : new SignedInSessionRow(current with { Current = true }, _time.GetUtcNow(), _time.LocalTimeZone);
            AddOthers(page.Items);
            SessionsMessage = "";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (load != _load)
            {
                return;
            }
            _logger.LogWarning("The sign-ins could not be read: {Failure}", ApiFailureText.Describe(ex));
            ClearSessions();
            SessionsMessage = "Couldn't load sessions. Try again.";
            CanRetrySessions = true;
        }
    }

    private void ClearSessions()
    {
        CurrentSession = null;
        OtherSessions.Clear();
        _nextCursor = null;
        CanLoadMoreSessions = false;
        CanRetrySessions = false;
        SessionsMessage = "";
    }

    // The household's sign-ins are the primary profile's to manage, or an administrator's before choosing a profile (the
    // web's RequirePrimaryOrAdmin). Null when the profiles could not be read to tell, so the page says so.
    private async Task<bool?> SeesSessionsAsync(Uri server, CancellationToken cancellationToken)
    {
        if (_session.ProfileId is not { } profileId)
        {
            return _session.Account?.IsAdmin == true;
        }
        try
        {
            var list = await _profiles.ListAsync(server, cancellationToken);
            return list.Items.FirstOrDefault(p => p.Id == profileId)?.IsPrimary == true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogInformation("The profiles could not be read to show the sign-ins: {Failure}", ApiFailureText.Describe(ex));
            return null;
        }
    }

    private void AddOthers(IEnumerable<LoginSession> sessions)
    {
        var now = _time.GetUtcNow();
        foreach (var session in sessions.Where(s => !s.Current && s.Id != CurrentSession?.Id && OtherSessions.All(o => o.Id != s.Id)))
        {
            OtherSessions.Add(new SignedInSessionRow(session, now, _time.LocalTimeZone));
        }
        OnPropertyChanged(nameof(HasNoOtherSessions));
    }

    private async Task LoadDevicesAsync(Uri server, int load, CancellationToken cancellationToken)
    {
        var rename = ReadRenameAsync(server, cancellationToken);
        try
        {
            var devices = await _devices.ListDevicesAsync(server, cancellationToken);
            if (load != _load)
            {
                return;
            }
            Group(devices);
            DevicesMessage = "";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (load != _load)
            {
                return;
            }
            _logger.LogWarning("The devices could not be read: {Failure}", ApiFailureText.Describe(ex));
            DeviceGroups.Clear();
            DevicesMessage = "Couldn't load your devices. Try again.";
        }
        _devicesRead = true;
        OnPropertyChanged(nameof(HasNoDevices));
        bool canRename = await rename;
        if (load == _load)
        {
            CanRename = canRename;
        }
    }

    private void ClearDevices()
    {
        DeviceGroups.Clear();
        DevicesMessage = "";
        _devicesRead = false;
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private async Task<bool> ReadRenameAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            return (await _settings.GetCapabilitiesAsync(server, cancellationToken)).SupportsDeviceRename;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogInformation("The settings capabilities could not be read: {Failure}", ApiFailureText.Describe(ex));
            return false;
        }
    }

    partial void OnCanRenameChanged(bool value)
    {
        foreach (var row in DeviceGroups.SelectMany(g => g.Devices))
        {
            row.CanRename = value;
        }
    }

    // As the web's device list: the one in use, those used this week, then the rest, each in the server's order. This PC is
    // the one in use when the server marks it, or when it carries this installation's device id.
    private void Group(IReadOnlyList<UserDevice> devices)
    {
        var now = _time.GetUtcNow();
        DeviceGroups.Clear();
        foreach (var group in devices.Select(d => (Device: d, Current: d.IsCurrentDevice || d.DeviceId == _identity.DeviceId))
                     .GroupBy(d => GroupOf(d.Device, d.Current, now)).OrderBy(g => g.Key))
        {
            DeviceGroups.Add(new UserDeviceGroup(GroupTitles[group.Key],
                [.. group.Select(d => new UserDeviceRow(d.Device, d.Current, now) { CanRename = CanRename })]));
        }
    }

    private static int GroupOf(UserDevice device, bool current, DateTimeOffset now) =>
        current ? 0 : device.LastSeenAt is { } seen && now - seen < TimeSpan.FromDays(7) ? 1 : 2;

    private static string? NextCursor(LoginSessionCollection page) =>
        page.Page is { HasMore: true, NextCursor: { Length: > 0 } next } ? next : null;
}
