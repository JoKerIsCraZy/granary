using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The Sign-in card, as the web's <c>SignInCard</c>: the username, the email, the role and whether the account can sign in.
/// Nobody changes their own role or disables themselves; only the Owner grants the admin role; the Owner stays an
/// enabled admin.
/// </summary>
public sealed partial class AdminUserDetailViewModel
{
    public AdminEditCard SignInCard { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInChanges))]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInChanges))]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInChanges), nameof(IsUserRole), nameof(RoleNote))]
    public partial bool IsAdminRole { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInChanges))]
    public partial bool CanSignIn { get; set; }

    /// <summary>The User choice of the role; picking it drops Admin.</summary>
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

    public bool HasSignInChanges => Account is { } account
        && (Username != account.Username || Email != account.Email || IsAdminRole != account.IsAdmin || CanSignIn != account.Enabled);

    /// <summary>The fields take changes: the viewer manages the account and the server offers it.</summary>
    public bool CanEditSignIn => Header is { Manageable: true } && Capabilities.Available;

    /// <summary>The role can change: never one's own.</summary>
    public bool CanChangeRole => CanEditSignIn && Header is { OwnAccount: false };

    /// <summary>Admin can be picked: by the Owner, or kept by an admin.</summary>
    public bool CanPickAdmin => CanChangeRole && (ViewerIsOwner || Account is { IsAdmin: true });

    /// <summary>Can sign in can change: never the Owner's or one's own.</summary>
    public bool CanToggleSignIn => CanEditSignIn && Account is { IsOwner: false } && Header is { OwnAccount: false };

    /// <summary>The line under the role, as the web's roleDescription.</summary>
    public string RoleNote
    {
        get
        {
            if (Account is not { } account)
            {
                return "";
            }
            if (Header is { OwnAccount: true })
            {
                return AppText.Get("web.admin.users.detail.access.signIn.ownRole");
            }
            if (!ViewerIsOwner && !account.IsAdmin)
            {
                return AppText.Get("web.admin.users.detail.access.signIn.ownerGrantsAdminRole");
            }
            if (IsAdminRole && !account.IsAdmin && account.AccessGroupId is { } group)
            {
                return AppText.Get("web.admin.users.detail.access.signIn.adminLeavesGroup",
                    ("group", Groups.FirstOrDefault(g => g.Id == group)?.Name ?? AppText.Get("web.admin.users.detail.header.groupIdFallback", ("id", group))));
            }
            return AppText.Get("web.admin.users.detail.access.signIn.ownerGrantsAdmin");
        }
    }

    /// <summary>The line under Can sign in, when it cannot change.</summary>
    public string SignInNote => Account switch
    {
        { IsOwner: true } => AppText.Get("web.admin.users.detail.access.signIn.ownerStaysEnabled"),
        _ when Header is { OwnAccount: true } => AppText.Get("web.admin.users.detail.access.signIn.cannotDisableSelf"),
        _ => "",
    };

    [RelayCommand]
    private Task SaveSignInAsync()
    {
        if (Account is not { } account)
        {
            return Task.CompletedTask;
        }
        string? validation = Username.Trim().Length == 0 ? AppText.Get("web.admin.users.detail.access.signIn.enterUsername")
            // An address is checked only when it changes: an account may have none.
            : Email != account.Email && !EmailAddress.IsValid(Email) ? EmailAddress.InvalidText
            : null;
        var change = new AdminUserChange
        {
            Username = Username != account.Username ? Username : null,
            Email = Email != account.Email ? Email : null,
            // An admin is never grouped: the server clears the group.
            Role = IsAdminRole != account.IsAdmin ? (IsAdminRole ? AdminRoles.Admin : AdminRoles.User) : null,
            Enabled = CanSignIn != account.Enabled ? CanSignIn : null,
        };
        return SaveAsync(SignInCard, change, validation);
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        ResetSignIn(null);
        SignInCard.Clear();
    }

    [RelayCommand]
    private Task ReviewSignInAsync() => ReviewAsync(SignInCard);

    // The fields edited on the account read before (null where untouched), to keep over a new read.
    private sealed record SignInEdits(string? Username, string? Email, bool? IsAdminRole, bool? CanSignIn);

    // The account as the Sign-in card sends it.
    private AdminUser SentSignIn(AdminUser account) => account with
    {
        Username = Username, Email = Email, Role = IsAdminRole ? AdminRoles.Admin : AdminRoles.User, Enabled = CanSignIn,
    };

    private SignInEdits? SignInDraft(AdminUser? before) => before is null ? null : new SignInEdits(
        Username != before.Username ? Username : null,
        Email != before.Email ? Email : null,
        IsAdminRole != before.IsAdmin ? IsAdminRole : null,
        CanSignIn != before.Enabled ? CanSignIn : null);

    private void ResetSignIn(SignInEdits? edits)
    {
        if (Account is not { } account)
        {
            return;
        }
        Username = edits?.Username ?? account.Username;
        Email = edits?.Email ?? account.Email;
        IsAdminRole = edits?.IsAdminRole ?? account.IsAdmin;
        CanSignIn = edits?.CanSignIn ?? account.Enabled;
        OnPropertyChanged(nameof(HasSignInChanges));
        OnPropertyChanged(nameof(CanEditSignIn));
        OnPropertyChanged(nameof(CanChangeRole));
        OnPropertyChanged(nameof(CanPickAdmin));
        OnPropertyChanged(nameof(CanToggleSignIn));
        OnPropertyChanged(nameof(RoleNote));
        OnPropertyChanged(nameof(SignInNote));
    }
}
