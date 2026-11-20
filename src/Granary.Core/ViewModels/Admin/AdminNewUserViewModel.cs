using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The web's Create User dialog (<c>components/admin/users/CreateUserForm.tsx</c>): the account, its role and group, the
/// two permissions and the first profile. Its limits are left to its group, as the dialog's Limits tab starts them. The
/// password is sent once and kept nowhere else.
/// </summary>
public sealed partial class AdminNewUserViewModel(AdminUsersApi users, ILogger<AdminNewUserViewModel> logger) : ObservableObject
{
    private bool _defaultProfileAvailable;
    // The account and profile this was opened as: everything it sends goes only as them.
    private AdminActor? _actor;

    [ObservableProperty]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequireChange))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool RequirePasswordChange { get; set; }

    /// <summary>The account is made an admin; only the Owner may.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPickGroup), nameof(IsGroupHintShown), nameof(IsUserRole))]
    public partial bool IsAdminRole { get; set; }

    /// <summary>The User choice of the role: picking it drops Admin; leaving it is Admin's to say.</summary>
    public bool IsUserRole
    {
        get => !IsAdminRole;
        set
        {
            if (value)
            {
                IsAdminRole = false;
            }
        }
    }

    [ObservableProperty]
    public partial IReadOnlyList<AdminGroupFilter> GroupChoices { get; private set; } = [];

    [ObservableProperty]
    public partial AdminGroupFilter? SelectedGroupChoice { get; set; }

    [ObservableProperty]
    public partial bool MarkerEdit { get; set; } = true;

    [ObservableProperty]
    public partial bool MetadataCuration { get; set; }

    [ObservableProperty]
    public partial bool CreateDefaultProfile { get; set; } = true;

    [ObservableProperty]
    public partial string Error { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    /// <summary>The viewer may make an admin.</summary>
    [ObservableProperty]
    public partial bool CanGrantAdmin { get; private set; }

    /// <summary>The Owner-only note under the role, for everyone else.</summary>
    public bool IsAdminLocked => !CanGrantAdmin;

    public bool CanRequireChange => Password.Length > 0;

    public bool CanPickGroup => !IsAdminRole && _groupsKnown;

    public bool IsGroupHintShown => IsAdminRole;

    public string DefaultProfileText => AppText.Get(_defaultProfileAvailable
        ? "web.admin.users.list.create.defaultProfile"
        : "web.admin.users.list.create.defaultProfileUnavailable");

    private bool _groupsKnown;

    /// <summary>Starts a new dialog with what the list read, as the account and profile the list was opened as.</summary>
    public void Start(AdminActor? actor, AdminUserCapabilities capabilities, IReadOnlyList<AdminAccessGroup> groups, bool groupsKnown, bool viewerIsOwner)
    {
        _actor = actor;
        Username = Email = Password = Error = "";
        RequirePasswordChange = IsAdminRole = MetadataCuration = false;
        MarkerEdit = CreateDefaultProfile = true;
        _defaultProfileAvailable = capabilities.DefaultProfile;
        CanGrantAdmin = viewerIsOwner;
        OnPropertyChanged(nameof(IsAdminLocked));
        OnPropertyChanged(nameof(DefaultProfileText));
        _groupsKnown = groupsKnown;
        // The picker starts on the default group, where the server would place the account anyway. A new account can't
        // choose "no group": the server puts it in the default group. Until the groups are known it says Default group.
        var defaultGroup = groups.FirstOrDefault(group => group.IsDefault);
        List<AdminGroupFilter> choices = [];
        if (!groupsKnown)
        {
            choices.Add(new AdminGroupFilter(AdminGroupFilter.None, AppText.Get("web.admin.users.list.create.defaultGroup")));
        }
        else if (defaultGroup is null)
        {
            choices.Add(new AdminGroupFilter(AdminGroupFilter.None, AppText.Get("web.admin.users.list.noGroup")));
        }
        choices.AddRange(groups.Select(group => new AdminGroupFilter(group.Id, group.Name)));
        GroupChoices = choices;
        SelectedGroupChoice = choices.FirstOrDefault(choice => choice.Value == defaultGroup?.Id) ?? choices.FirstOrDefault();
        OnPropertyChanged(nameof(CanPickGroup));
    }

    /// <summary>Creates the account; true when it was made and the dialog may close.</summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (IsSaving || _actor is not { } actor || (CreateDefaultProfile && !_defaultProfileAvailable))
        {
            return false;
        }
        if (!EmailAddress.IsValid(Email))
        {
            Error = EmailAddress.InvalidText;
            return false;
        }
        IsSaving = true;
        Error = "";
        try
        {
            await users.CreateAsync(actor, Draft(), cancellationToken);
            Password = "";
            return true;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not create an account");
            Error = ex is SiloApiException { Status: 409 }
                ? AppText.Get("granary.admin.users.nameTaken")
                : AdminFailures.Describe(ex, AppText.Get("web.admin.users.list.create.saveFailed"));
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private AdminUserDraft Draft()
    {
        List<string> permissions = [];
        if (MarkerEdit)
        {
            permissions.Add(AdminPermissions.MarkerEdit);
        }
        if (MetadataCuration)
        {
            permissions.Add(AdminPermissions.MetadataCuration);
        }
        // An admin stays ungrouped; "no group" or a group still unknown lets the server choose.
        string? group = IsAdminRole || SelectedGroupChoice is not { Value: not AdminGroupFilter.None } choice ? null : choice.Value;
        return new AdminUserDraft
        {
            Username = Username,
            Email = Email,
            Password = Password,
            RequirePasswordChange = RequirePasswordChange && Password.Length > 0 ? true : null,
            Role = IsAdminRole ? AdminRoles.Admin : AdminRoles.User,
            Permissions = permissions,
            CreateDefaultProfile = CreateDefaultProfile,
            AccessGroupId = group,
        };
    }
}
