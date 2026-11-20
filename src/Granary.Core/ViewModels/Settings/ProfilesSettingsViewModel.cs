using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Profiles;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Profiles, as the web's <c>pages/settings/ProfilesSettings.tsx</c>: the household's profiles with their badges
/// and limits, New profile while there is room, and each profile's Edit and Delete. Only the household's primary profile
/// sees the page (docs/api-contract.md, 6.7), and its writes go out as that profile. What changes here, the sidebar's
/// profile entry and menu read again.
/// </summary>
public sealed partial class ProfilesSettingsViewModel : ObservableObject
{
    private readonly ProfilesApi _profiles;
    private readonly ISessionManager _session;
    private readonly IConfirmation _confirmation;
    private readonly SidebarViewModel? _sidebar;
    private readonly ILogger<ProfilesSettingsViewModel> _logger;

    // The server, account and acting profile the list was read for; a write goes out only while they still are.
    private (ServerEntry Server, string AccountId, string ProfileId)? _target;
    private int _load;

    public ProfilesSettingsViewModel(ProfilesApi profiles, ISessionManager session, IConfirmation confirmation, ILogger<ProfilesSettingsViewModel> logger,
        SidebarViewModel? sidebar = null)
    {
        _profiles = profiles;
        _session = session;
        _confirmation = confirmation;
        _logger = logger;
        _sidebar = sidebar;
    }

    public ObservableCollection<ProfileSettingsRow> Profiles { get; } = [];

    /// <summary>The household as last read, with what a profile can carry; null before the first read.</summary>
    public ProfileCollection? Household { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsList))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The list was read: an empty one then means the account has no profiles.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowsList))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>"No profiles yet", as the web's empty list.</summary>
    public bool IsEmpty => IsLoaded && Profiles.Count == 0;

    public bool ShowsList => IsLoaded && !IsLoading;

    /// <summary>The last outcome, as the web's toasts say it ("Profile deleted"), or why the list could not be read.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>The list could not be read; the page offers Retry.</summary>
    [ObservableProperty]
    public partial bool HasLoadError { get; private set; }

    /// <summary>New profile shows: the household has room, as far as the server said.</summary>
    [ObservableProperty]
    public partial bool CanAdd { get; private set; }

    /// <summary>"This account can have 5 profiles. An administrator can raise the limit." once the household is full.</summary>
    [ObservableProperty]
    public partial string LimitText { get; private set; } = "";

    /// <summary>Reads the household for the server and profile in use.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        int load = ++_load;
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server, Account.Id: { } accountId, ProfileId: { } profileId })
        {
            _target = null;
            Show(null, null);
            return;
        }
        IsLoading = true;
        HasLoadError = false;
        try
        {
            var household = await _profiles.ListAsync(server.BaseUri, cancellationToken);
            if (load != _load)
            {
                return;
            }
            _target = (server, accountId, profileId);
            Show(household, server.BaseUri);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (load != _load)
            {
                return;
            }
            _logger.LogWarning("Settings → Profiles could not read the household: {Failure}", ApiFailureText.Describe(ex));
            Status = LibraryErrors.Describe(ex);
            HasLoadError = true;
        }
        finally
        {
            if (load == _load)
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        Status = "";
        return LoadAsync();
    }

    /// <summary>
    /// Deletes a profile after the web's question ("Delete profile "Elena"? This action cannot be undone."). One that is gone
    /// already leaves the list too; any other failure keeps it and says so.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeleteAsync(ProfileSettingsRow? row)
    {
        if (row is null || row.IsBusy || !row.CanDelete || Target() is not { } target || !Profiles.Contains(row))
        {
            return;
        }
        bool asked = await _confirmation.AskAsync(AppText.Get("web.settings.profiles.deleteTitle"),
            AppText.Get("web.settings.profiles.deleteDescription", ("name", row.Name)), AppText.Get("web.settings.profiles.delete"));
        if (!asked || Target() is not { } now || now != target || !Profiles.Contains(row))
        {
            return;
        }
        row.IsBusy = true;
        Status = "";
        try
        {
            await _profiles.DeleteAsync(target.Server.BaseUri, row.Id, ManagingProfile.Acting(target.AccountId, target.ProfileId));
            Status = AppText.Get("web.common.toast.profiles.deleted");
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Deleted elsewhere meanwhile.
            Status = AppText.Get("web.common.toast.profiles.deleted");
        }
        catch (Exception ex) when (ProfileWriteText.IsRefusal(ex, CancellationToken.None))
        {
            _logger.LogWarning("A profile could not be deleted: {Failure}", ApiFailureText.Describe(ex));
            Status = ProfileWriteText.Describe(ex, "web.common.toast.profiles.deleteFailed");
            return;
        }
        finally
        {
            row.IsBusy = false;
        }
        await AfterChangeAsync();
    }

    /// <summary>
    /// The editor for New profile (<paramref name="row"/> null) or a profile's Edit, writing as the acting primary profile;
    /// null when the list no longer belongs to the server, account and profile in use, or a new one has no room.
    /// </summary>
    public ProfileEditorViewModel? CreateEditor(ProfileSettingsRow? row)
    {
        if (Target() is not { } target || Household is not { } household || (row is null ? !household.HasRoom : !Profiles.Contains(row)))
        {
            return null;
        }
        return new ProfileEditorViewModel(_profiles,
            new ProfileEditorTarget(target.Server.BaseUri, ManagingProfile.Acting(target.AccountId, target.ProfileId), household), row?.Profile, _logger);
    }

    /// <summary>
    /// After the editor saved: the web's toast, and when the acting profile's own PIN changed, the same profile again with a
    /// proof of the new PIN (as the web's <c>onSaveSuccess</c>), since a PIN change ends its old one. Then the list and the
    /// sidebar read the household again.
    /// </summary>
    public async Task AfterSaveAsync(ProfileEditorSaved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        Status = saved.Notice.Length > 0 ? saved.Notice
            : AppText.Get(saved.Created ? "web.common.toast.profiles.created" : "web.common.toast.profiles.updated");
        if (Target() is { } target && saved.Profile.Id == target.ProfileId && (saved.NewPin.Length > 0 || saved.PinCleared))
        {
            await ProveOwnPinAsync(target, saved.PinCleared ? null : saved.NewPin);
        }
        await AfterChangeAsync();
    }

    // The acting profile picked again with the new PIN's proof, or without one once it has none; only while it still acts.
    private async Task ProveOwnPinAsync((ServerEntry Server, string AccountId, string ProfileId) target, string? pin)
    {
        try
        {
            string? token = null;
            if (pin is not null)
            {
                var verification = await _profiles.VerifyPinAsync(target.Server.BaseUri, target.ProfileId, pin);
                if (!verification.Valid || string.IsNullOrEmpty(verification.ProfileToken))
                {
                    Status = AppText.Get("web.settings.profiles.savedPinFailed");
                    return;
                }
                token = verification.ProfileToken;
            }
            if (Target() != target)
            {
                return;
            }
            await _session.SelectProfileAsync(target.Server.Id, target.ProfileId, token);
        }
        catch (SiloApiException ex) when (ex.Status == 429)
        {
            _logger.LogWarning("The new PIN could not be checked: {Failure}", ApiFailureText.Describe(ex));
            Status = AppText.Get("web.settings.profiles.savedWithLockout", ("lockout", PinLockout(ex.RetryAfter)));
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is ActiveServerChangedException)
        {
            _logger.LogWarning("The new PIN could not be checked: {Failure}", ApiFailureText.Describe(ex));
            Status = AppText.Get("web.settings.profiles.savedPinFailed");
        }
    }

    // A PIN check refused with 429, in the web's words (components/profiles/pinLockout.ts).
    private static string PinLockout(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } delay)
        {
            return AppText.Get("web.profiles.pin.lockedLater");
        }
        int minutes = ApiFailureText.WholeMinutes(delay);
        return AppText.Get("web.profiles.pin.lockedMinutes", ("count", minutes), ("count_text", AppText.Number(minutes)));
    }

    /// <summary>The list and the sidebar read the household again, as the web refetches it after a write.</summary>
    private async Task AfterChangeAsync()
    {
        _sidebar?.ReadAgain();
        string status = Status;
        await LoadAsync();
        if (!HasLoadError)
        {
            Status = status;
        }
    }

    // The server and profile the list belongs to, while they are still the ones in use.
    private (ServerEntry Server, string AccountId, string ProfileId)? Target() =>
        _target is { } target && _session.State == SessionState.Ready && _session.ActiveServer?.Id == target.Server.Id
            && _session.Account?.Id == target.AccountId && _session.ProfileId == target.ProfileId
            ? target
            : null;

    private void Show(ProfileCollection? household, Uri? serverBase)
    {
        // Only the household's primary profile manages it (the server refuses anyone else): for another, the page stays empty,
        // offers no editor and deletes nothing, however it was reached.
        if (household is { Items.Count: > 0 } && !household.Items.Any(p => p.Id == _target?.ProfileId && p.IsPrimary))
        {
            household = null;
        }
        Household = household;
        Profiles.Clear();
        if (household is not null && serverBase is not null)
        {
            string? acting = _target?.ProfileId;
            foreach (var profile in household.Items)
            {
                Profiles.Add(new ProfileSettingsRow(profile, serverBase, profile.Id == acting,
                    ProfileSettingsRow.GuardOf(profile, household.Items.Count, acting)));
            }
        }
        IsLoaded = household is not null;
        CanAdd = household?.HasRoom == true;
        LimitText = household is { HasRoom: false, MaxProfiles: { } limit }
            ? AppText.Get("web.settings.profiles.limitReached", ("count", limit))
            : "";
        OnPropertyChanged(nameof(IsEmpty));
    }
}
