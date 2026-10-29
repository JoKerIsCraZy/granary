using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
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
/// without a profile sees them.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private const string SignOutNote = "A video already playing may continue until its next request. Saved device settings are kept.";

    // As the web's device list groups them, by how recently each was used.
    private static readonly string[] GroupTitles = ["Using now", "This week", "Earlier"];

    private readonly ISessionManager _session;
    private readonly DevicesApi _devices;
    private readonly SettingsApi _settings;
    private readonly ProfilesApi _profiles;
    private readonly IConfirmation _confirmation;
    private readonly TimeProvider _time;
    private readonly ILogger<DevicesViewModel> _logger;
    private string? _nextCursor;
    private bool _devicesRead;

    public DevicesViewModel(ISessionManager session, DevicesApi devices, SettingsApi settings, ProfilesApi profiles, IConfirmation confirmation,
        TimeProvider time, ILogger<DevicesViewModel> logger)
    {
        _session = session;
        _devices = devices;
        _settings = settings;
        _profiles = profiles;
        _confirmation = confirmation;
        _time = time;
        _logger = logger;
        OtherSessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoOtherSessions));
        DeviceGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoDevices));
    }

    /// <summary>The account's sign-ins show: the primary profile, or an administrator without a profile.</summary>
    [ObservableProperty]
    public partial bool ShowsSessions { get; private set; }

    /// <summary>"Loading sessions…", a failure, or that the server has no session management; empty once the list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessionsMessage), nameof(HasNoOtherSessions))]
    public partial string SessionsMessage { get; private set; } = "";

    public bool HasSessionsMessage => SessionsMessage.Length > 0;

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
    public bool HasNoOtherSessions => ShowsSessions && !HasSessionsMessage && OtherSessions.Count == 0;

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

    partial void OnCanRenameChanged(bool value)
    {
        foreach (var row in DeviceGroups.SelectMany(g => g.Devices))
        {
            row.CanRename = value;
        }
    }

    /// <summary>What the last action did: "Session signed out", "Couldn't forget", …</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>Reads the sign-ins and the devices.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        await Task.WhenAll(LoadSessionsAsync(server.BaseUri, cancellationToken), LoadDevicesAsync(server.BaseUri, cancellationToken));
    }

    /// <summary>Reads both lists again, as the web's Refresh.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadMoreSessionsAsync()
    {
        if (_session.ActiveServer is not { } server || _nextCursor is not { } cursor)
        {
            return;
        }
        try
        {
            var page = await _devices.ListSessionsAsync(server.BaseUri, cursor);
            // The same cursor again would load the same page forever.
            string? next = NextCursor(page);
            _nextCursor = next == cursor ? null : next;
            CanLoadMoreSessions = _nextCursor is not null;
            AddOthers(page.Items);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("More sign-ins could not be read: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't load more sessions. Reload the list.";
        }
    }

    /// <summary>
    /// Signs a sign-in out after asking. Another one loses access on its next request; Granary's own signs Granary out
    /// of the server, back to the sign-in screen, as the web's own does for its browser.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SignOutSessionAsync(SignedInSessionRow? row)
    {
        if (row is null || row.IsBusy || _session.ActiveServer is not { } server)
        {
            return;
        }
        bool asked = row.IsCurrent
            ? await _confirmation.AskAsync("Sign out this device?",
                "You'll return to the sign-in screen. You can sign in again with the same password or sign-in provider.\n\n" + SignOutNote, "Sign out")
            : await _confirmation.AskAsync($"Sign out {row.Name}?",
                "This client will lose access on its next authenticated request. Its other sessions and password stay unchanged.\n\n" + SignOutNote,
                "Sign out");
        if (!asked)
        {
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            if (row.IsCurrent)
            {
                await _session.SignOutAsync();
                return;
            }
            await _devices.SignOutSessionAsync(server.BaseUri, row.Id);
            OtherSessions.Remove(row);
            Status = "Session signed out";
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Already gone: signed out elsewhere, or expired.
            OtherSessions.Remove(row);
            Status = "Session signed out";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A sign-in could not be signed out: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't sign out the session. Try again.";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Saves the name typed for a device; an empty one shows the name it reports again.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveRenameAsync(UserDeviceRow? row)
    {
        if (row is null || row.IsBusy || _session.ActiveServer is not { } server)
        {
            return;
        }
        // The server counts characters, not UTF-16 units, and takes at most 120.
        string typed = string.Concat(row.EditName.Trim().EnumerateRunes().Take(120));
        string? name = typed.Length > 0 ? typed : null;
        if (name == row.Device.CustomName)
        {
            row.EndRename();
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            var saved = await _devices.RenameDeviceAsync(server.BaseUri, row.DeviceId, name);
            row.Show(row.Device with { CustomName = saved.CustomName });
            row.EndRename();
            Status = name is null ? "Device name reset" : "Device renamed";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A device could not be renamed: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't rename";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Removes the settings changed on a device after asking, so it follows the profile's again.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task UseProfileSettingsAsync(UserDeviceRow? row)
    {
        if (row is null || row.IsBusy || !row.CanUseProfileSettings || _session.ActiveServer is not { } server)
        {
            return;
        }
        int changed = row.Device.ChangedCount;
        if (!await _confirmation.AskAsync($"Use your profile settings on {row.Name}?",
                $"This removes the {DeviceText.Count(changed, "setting")} changed on this device. Settings that only apply to devices go back to the app default.",
                "Use profile settings"))
        {
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            await _devices.ClearDeviceSettingsAsync(server.BaseUri, row.DeviceId);
            Status = "Removed the changes on this device";
            await LoadDevicesAsync(server.BaseUri, CancellationToken.None);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A device's settings could not be cleared: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't clear";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Forgets a device after asking: its settings go, and it leaves the list until it is used again. Not this one.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ForgetAsync(UserDeviceRow? row)
    {
        if (row is null || row.IsBusy || !row.CanForget || _session.ActiveServer is not { } server)
        {
            return;
        }
        if (!await _confirmation.AskAsync($"Forget {row.Name}?", "Its settings are removed and it disappears from this list until it's used again.",
                "Forget"))
        {
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            await _devices.ForgetDeviceAsync(server.BaseUri, row.DeviceId);
            Remove(row);
            Status = "Device forgotten";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A device could not be forgotten: {Failure}", ApiFailureText.Describe(ex));
            Status = "Couldn't forget";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private async Task LoadSessionsAsync(Uri server, CancellationToken cancellationToken)
    {
        ShowsSessions = await SeesSessionsAsync(server, cancellationToken);
        if (!ShowsSessions)
        {
            return;
        }
        CanRetrySessions = false;
        SessionsMessage = "Loading sessions…";
        try
        {
            var capabilities = await _devices.GetSessionCapabilitiesAsync(server, cancellationToken);
            if (!capabilities.Available)
            {
                SessionsMessage = "Signed-in session management is unavailable on this server.";
                return;
            }
            var page = await _devices.ListSessionsAsync(server, null, cancellationToken);
            _nextCursor = NextCursor(page);
            CanLoadMoreSessions = _nextCursor is not null;
            var current = page.CurrentSession ?? page.Items.FirstOrDefault(s => s.Current);
            var now = _time.GetUtcNow();
            CurrentSession = current is null ? null : new SignedInSessionRow(current with { Current = true }, now, _time.LocalTimeZone);
            OtherSessions.Clear();
            AddOthers(page.Items);
            SessionsMessage = "";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("The sign-ins could not be read: {Failure}", ApiFailureText.Describe(ex));
            SessionsMessage = "Couldn't load sessions. Try again.";
            CanRetrySessions = true;
        }
    }

    // The household's sign-ins are the primary profile's to manage, or an administrator's before choosing a profile.
    private async Task<bool> SeesSessionsAsync(Uri server, CancellationToken cancellationToken)
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
            return false;
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

    private async Task LoadDevicesAsync(Uri server, CancellationToken cancellationToken)
    {
        var rename = ReadRenameAsync(server, cancellationToken);
        try
        {
            var devices = await _devices.ListDevicesAsync(server, cancellationToken);
            Group(devices);
            DevicesMessage = "";
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("The devices could not be read: {Failure}", ApiFailureText.Describe(ex));
            DevicesMessage = "Couldn't load your devices. Try again.";
        }
        _devicesRead = true;
        OnPropertyChanged(nameof(HasNoDevices));
        CanRename = await rename;
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

    // As the web's device list: the one in use, those used this week, then the rest, each in the server's order.
    private void Group(IReadOnlyList<UserDevice> devices)
    {
        var now = _time.GetUtcNow();
        DeviceGroups.Clear();
        foreach (var group in devices.GroupBy(d => GroupOf(d, now)).OrderBy(g => g.Key))
        {
            DeviceGroups.Add(new UserDeviceGroup(GroupTitles[group.Key], [.. group.Select(d => new UserDeviceRow(d, now) { CanRename = CanRename })]));
        }
    }

    private static int GroupOf(UserDevice device, DateTimeOffset now) =>
        device.IsCurrentDevice ? 0 : device.LastSeenAt is { } seen && now - seen < TimeSpan.FromDays(7) ? 1 : 2;

    private void Remove(UserDeviceRow row)
    {
        if (DeviceGroups.FirstOrDefault(g => g.Devices.Contains(row)) is not { } group)
        {
            return;
        }
        group.Devices.Remove(row);
        if (group.Devices.Count == 0)
        {
            DeviceGroups.Remove(group);
        }
    }

    private static string? NextCursor(LoginSessionCollection page) =>
        page.Page is { HasMore: true, NextCursor: { Length: > 0 } next } ? next : null;
}
