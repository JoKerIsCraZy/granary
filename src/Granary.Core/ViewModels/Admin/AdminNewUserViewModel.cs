using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The web's Create User dialog (<c>components/admin/users/CreateUserForm.tsx</c>): its Account tab (the name, address,
/// password and role), its Access tab (the group, the two permissions, the libraries, downloads and requests) and its Limits
/// tab, the last two through <see cref="Policy"/>, which sends only what is overridden. The password is sent once and kept
/// nowhere else.
/// </summary>
public sealed partial class AdminNewUserViewModel(AdminUsersApi users, AdminApi admin, ILogger<AdminNewUserViewModel> logger) : ObservableObject
{
    /// <summary>The dialog's tabs, as the web's <c>dialogTab</c>.</summary>
    public const int AccountTab = 0;
    public const int AccessTab = 1;
    public const int LimitsTab = 2;

    private bool _defaultProfileAvailable;
    // The account and profile this was opened as: everything it sends goes only as them.
    private AdminActor? _actor;
    private IReadOnlyList<AdminAccessGroup> _groups = [];
    private AdminPolicyDefaults? _defaults;
    // Counts the starts: only the latest one's reads are shown.
    private int _starts;
    private bool _groupsKnown;

    [ObservableProperty]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequireChange), nameof(RequirePasswordChangeShown))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequirePasswordChangeShown))]
    public partial bool RequirePasswordChange { get; set; }

    /// <summary>The role menu: 0 User, 1 Admin (the Owner only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdminRole), nameof(CanPickGroup), nameof(IsGroupHintShown))]
    public partial int RoleIndex { get; set; }

    /// <summary>The account is made an admin; only the Owner may.</summary>
    public bool IsAdminRole
    {
        get => RoleIndex == 1;
        set => RoleIndex = value ? 1 : 0;
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

    /// <summary>The tab shown: <see cref="AccountTab"/>, <see cref="AccessTab"/> or <see cref="LimitsTab"/>.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    [ObservableProperty]
    public partial string Error { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    public partial bool IsSaving { get; private set; }

    /// <summary>The viewer may make an admin.</summary>
    [ObservableProperty]
    public partial bool CanGrantAdmin { get; private set; }

    /// <summary>The Access and Limits tabs' policy fields.</summary>
    public AdminPolicyForm Policy { get; } = new();

    /// <summary>The Owner-only note under the role, for everyone else.</summary>
    public bool IsAdminLocked => !CanGrantAdmin;

    public bool CanRequireChange => Password.Length > 0;

    /// <summary>The switch shows on only while there is a password to make temporary, as the web's.</summary>
    public bool RequirePasswordChangeShown
    {
        get => RequirePasswordChange && Password.Length > 0;
        set => RequirePasswordChange = value;
    }

    public bool CanPickGroup => !IsAdminRole && _groupsKnown;

    public bool IsGroupHintShown => IsAdminRole;

    public string DefaultProfileText => AppText.Get(_defaultProfileAvailable
        ? "web.admin.users.list.create.defaultProfile"
        : "web.admin.users.list.create.defaultProfileUnavailable");

    public string SaveText => AppText.Get(IsSaving ? "web.admin.users.list.create.saving" : "common.save");

    /// <summary>Starts a new dialog with what the list read, as the account and profile the list was opened as.</summary>
    public void Start(AdminActor? actor, AdminUserCapabilities capabilities, IReadOnlyList<AdminAccessGroup> groups, bool groupsKnown, bool viewerIsOwner)
    {
        _starts++;
        _actor = actor;
        _groups = groups;
        _defaults = null;
        Username = Email = Password = Error = "";
        RequirePasswordChange = MetadataCuration = false;
        RoleIndex = 0;
        SelectedTab = AccountTab;
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
        // Groups not read this time may be out of date: the server places the account then.
        if (groupsKnown)
        {
            choices.AddRange(groups.Select(group => new AdminGroupFilter(group.Id, group.Name)));
        }
        GroupChoices = choices;
        SelectedGroupChoice = choices.FirstOrDefault(choice => choice.Value == defaultGroup?.Id) ?? choices.FirstOrDefault();
        OnPropertyChanged(nameof(CanPickGroup));
        Policy.Reset([]);
        Policy.ShowServerSwitches(null);
        HintPolicy();
    }

    /// <summary>
    /// Reads what the Access and Limits tabs need beyond the list's reads, as the web's dialog does: the libraries, the
    /// server's built-in policy layers and its download switches. A part that fails leaves its fields on their defaults.
    /// </summary>
    public async Task ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_actor is not { } actor)
        {
            return;
        }
        int start = _starts;
        var libraries = Try(admin.ListLibrariesAsync(actor, cancellationToken), "libraries");
        var defaults = Try(users.GetPolicyDefaultsAsync(actor, cancellationToken), "policy defaults");
        var switches = Try(users.GetServerSwitchesAsync(actor, cancellationToken), "download switches");
        await Task.WhenAll(libraries, defaults, switches);
        if (start != _starts)
        {
            return;
        }
        if (await libraries is { } list)
        {
            Policy.ShowLibraries(list.Items.OrderBy(library => library.SortOrder)
                .Select(library => new AdminLibraryChoice(library.Id, library.Name, library.Type, library.Enabled) { IsChecked = true }));
        }
        _defaults = await defaults;
        Policy.ShowServerSwitches(await switches);
        HintPolicy();
    }

    /// <summary>Creates the account; true when it was made and the dialog may close.</summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (IsSaving || _actor is not { } actor || (CreateDefaultProfile && !_defaultProfileAvailable))
        {
            return false;
        }
        if (Username.Trim().Length == 0)
        {
            Error = AppText.Get("web.admin.users.detail.access.signIn.enterUsername");
            SelectedTab = AccountTab;
            return false;
        }
        if (!EmailAddress.IsValid(Email.Trim()))
        {
            Error = EmailAddress.InvalidText;
            SelectedTab = AccountTab;
            return false;
        }
        // A cleared or half-typed limit would send the value before it; the Limits tab shows which, as the web's.
        if (Policy.HasInvalidLimit)
        {
            SelectedTab = LimitsTab;
            return false;
        }
        IsSaving = true;
        Error = "";
        try
        {
            await users.CreateAsync(actor, Policy.ApplyTo(Draft()), cancellationToken);
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

    partial void OnRoleIndexChanged(int oldValue, int newValue)
    {
        // A menu clears its choice when its items change; and only the Owner makes an admin.
        if (newValue < 0 || (newValue == 1 && !CanGrantAdmin))
        {
            RoleIndex = oldValue < 0 ? 0 : oldValue;
            return;
        }
        HintPolicy();
    }

    partial void OnSelectedGroupChoiceChanged(AdminGroupFilter? value) => HintPolicy();

    // The defaults follow the role and group picked, not the ones saved, as the web's policyInheritHints.
    private void HintPolicy()
    {
        if (IsAdminRole)
        {
            Policy.Hint(AdminPolicySource.Admin, _defaults?.Admin);
        }
        else if (!_groupsKnown)
        {
            // The default group the account joins is unknown, and so is what it inherits.
            Policy.Hint(AdminPolicySource.Group, null);
        }
        else if (SelectedGroupChoice is not { Value: not AdminGroupFilter.None } choice)
        {
            Policy.Hint(AdminPolicySource.Server, _defaults?.Ungrouped);
        }
        else
        {
            Policy.Hint(AdminPolicySource.Group, _groups.FirstOrDefault(group => group.Id == choice.Value)?.Policy);
        }
    }

    private async Task<T?> Try<T>(Task<T> read, string what) where T : class
    {
        try
        {
            return await read;
        }
        catch (Exception ex) when (AdminFailures.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not read the {What} for a new account", what);
            return null;
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
            Username = Username.Trim(),
            Email = Email.Trim(),
            Password = Password,
            RequirePasswordChange = RequirePasswordChange && Password.Length > 0 ? true : null,
            Role = IsAdminRole ? AdminRoles.Admin : AdminRoles.User,
            Permissions = permissions,
            CreateDefaultProfile = CreateDefaultProfile,
            AccessGroupId = group,
        };
    }
}
