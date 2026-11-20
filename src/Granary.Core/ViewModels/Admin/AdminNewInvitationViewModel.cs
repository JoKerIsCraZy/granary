using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The web's Invite someone dialog (<c>CreateInvitationForm.tsx</c>): a link to share or an email, the address, the role,
/// the group and libraries, a note, the first profile and the tour. Once made, the dialog shows the claim link: the server
/// discloses it this once. A failed create may still have made one, so the history is read again before another try.
/// </summary>
public sealed partial class AdminNewInvitationViewModel(ISessionManager session, AdminInvitationsApi invitations, AdminApi admin,
    ILogger<AdminNewInvitationViewModel> logger) : ObservableObject
{
    private bool _defaultProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLink), nameof(NoteLabel), nameof(NoteHint), nameof(ProfileHint), nameof(SubmitText))]
    public partial bool IsEmail { get; set; }

    [ObservableProperty]
    public partial bool EmailDelivery { get; private set; }

    [ObservableProperty]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    public partial string EmailError { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUserRole), nameof(CanPickGroup))]
    public partial bool IsAdminRole { get; set; }

    [ObservableProperty]
    public partial bool CanGrantAdmin { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<AdminGroupFilter> GroupChoices { get; private set; } = [];

    [ObservableProperty]
    public partial AdminGroupFilter? SelectedGroup { get; set; }

    /// <summary>The account takes its group's libraries; off, the ones ticked here.</summary>
    [ObservableProperty]
    public partial bool InheritLibraries { get; set; } = true;

    public ObservableCollection<AdminLibraryChoice> Libraries { get; } = [];

    [ObservableProperty]
    public partial string Note { get; set; } = "";

    [ObservableProperty]
    public partial bool CreateProfile { get; set; }

    [ObservableProperty]
    public partial bool ShowTour { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmitText), nameof(CanSubmit))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial string Error { get; private set; } = "";

    /// <summary>A failed create: the history is read again (<see cref="ReloadDone"/>) before another try.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial bool NeedsReload { get; private set; }

    /// <summary>The invitation made, with its claim link; null until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultMessage), nameof(FinePrint), nameof(ClaimUrl), nameof(CanSubmit), nameof(HasResult))]
    public partial InvitationDelivery? Result { get; private set; }

    public bool IsLink
    {
        get => !IsEmail;
        set
        {
            if (value)
            {
                IsEmail = false;
            }
        }
    }

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

    public bool CanPickGroup => !IsAdminRole;

    /// <summary>The server cannot make the profile with the account: only a profileless invitation can be made.</summary>
    public bool HasNoDefaultProfile => !_defaultProfile;

    public string NoteLabel => AppText.Get(IsEmail ? "web.admin.settings.invitations.create.personalNoteLabel" : "web.admin.settings.invitations.create.noteLabel");

    public string NoteHint => AppText.Get(IsEmail ? "web.admin.settings.invitations.create.noteHintEmail" : "web.admin.settings.invitations.create.noteHintLink");

    public string ProfileHint => AppText.Get(IsEmail ? "web.admin.settings.invitations.create.profileHintEmail" : "web.admin.settings.invitations.create.profileHintLink");

    public string EmailOptionBody => AppText.Get(EmailDelivery
        ? "web.admin.settings.invitations.create.emailOptionBody"
        : "web.admin.settings.invitations.create.emailOptionUnavailable");

    public string SubmitText => AppText.Get((IsEmail, IsSaving) switch
    {
        (false, false) => "web.admin.settings.invitations.create.submitLink",
        (false, true) => "web.admin.settings.invitations.create.creating",
        (true, false) => "web.admin.settings.invitations.create.submitEmail",
        _ => "web.admin.settings.invitations.create.sending",
    });

    /// <summary>Without the server's default profile the switch only turns off, as the web's.</summary>
    public bool CanChangeProfile => _defaultProfile || CreateProfile;

    public bool CanSubmit => !IsSaving && !NeedsReload && (_defaultProfile || !CreateProfile) && Result is null;

    public string ResultMessage => Result is { } result ? AdminInvitationRow.DeliveryMessage(result) : "";

    public string FinePrint => AppText.Get(Result is { DeliveryStatus: "not_requested" }
        ? "web.admin.settings.invitations.create.finePrintLink"
        : "web.admin.settings.invitations.create.finePrintEmail");

    public bool HasResult => Result is not null;

    /// <summary>The claim link, shown once.</summary>
    public string ClaimUrl => Result?.ClaimUrl ?? "";

    /// <summary>Starts a new dialog with what the tabs read, and reads the libraries to choose from.</summary>
    public async Task StartAsync(AdminInvitationCapabilities capabilities, IReadOnlyList<AdminAccessGroup> groups, bool viewerIsOwner)
    {
        _defaultProfile = capabilities.DefaultProfile;
        EmailDelivery = capabilities.EmailDelivery;
        IsEmail = capabilities.EmailDelivery;
        Email = EmailError = Note = Error = "";
        IsAdminRole = NeedsReload = false;
        CanGrantAdmin = viewerIsOwner;
        CreateProfile = capabilities.DefaultProfile;
        ShowTour = InheritLibraries = true;
        Result = null;
        // The default group first, named so; without one, the server's default.
        var defaultGroup = groups.FirstOrDefault(group => group.IsDefault);
        GroupChoices =
        [
            new(AdminGroupFilter.None, defaultGroup is null
                ? AppText.Get("web.admin.settings.invitations.create.serverDefault")
                : AppText.Get("web.admin.settings.invitations.create.defaultGroup", ("name", defaultGroup.Name))),
            .. groups.Where(group => !group.IsDefault).Select(group => new AdminGroupFilter(group.Id, group.Name)),
        ];
        SelectedGroup = GroupChoices[0];
        OnPropertyChanged(nameof(HasNoDefaultProfile));
        OnPropertyChanged(nameof(CanChangeProfile));
        OnPropertyChanged(nameof(EmailOptionBody));
        OnPropertyChanged(nameof(CanSubmit));
        Libraries.Clear();
        if (session.ActiveServer is not { } server)
        {
            return;
        }
        try
        {
            foreach (var library in (await admin.ListLibrariesAsync(server.BaseUri)).Items.OrderBy(library => library.SortOrder))
            {
                Libraries.Add(new AdminLibraryChoice(library.Id, library.Name) { IsChecked = true });
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            // Without them the invitation takes its group's libraries, which it does by default.
            logger.LogWarning(ex, "Could not read the libraries for an invitation");
        }
    }

    /// <summary>Makes the invitation; true when it was made and its link shows.</summary>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSubmit || session.ActiveServer is not { } server)
        {
            return false;
        }
        if (IsEmail && !EmailAddress.IsValid(Email))
        {
            EmailError = EmailAddress.InvalidText;
            return false;
        }
        EmailError = Error = "";
        IsSaving = true;
        try
        {
            Result = await invitations.CreateAsync(server.BaseUri, Draft(), cancellationToken);
            return true;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not create an invitation");
            // Links are built on the server's public URL, and a server without one refuses them.
            Error = ex is SiloApiException { Code: ProblemCodes.CapabilityNotConfigured } && !IsEmail
                ? AdminText.WithoutLink("web.admin.settings.invitations.publicUrlMissing")
                : AdminFailures.Describe(ex, AppText.Get("web.admin.settings.invitations.create.failed"));
            NeedsReload = true;
            return false;
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(CanSubmit));
        }
    }

    /// <summary>The history was read again after a failure: another try may go.</summary>
    public void ReloadDone()
    {
        NeedsReload = false;
        Error = "";
        OnPropertyChanged(nameof(CanSubmit));
    }

    /// <summary>Forgets the claim link when the dialog closes.</summary>
    public void Clear() => Result = null;

    partial void OnCreateProfileChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(CanChangeProfile));
    }

    partial void OnInheritLibrariesChanged(bool value)
    {
        // Choosing starts from every library, as the web's selector does.
        if (!value)
        {
            foreach (var library in Libraries)
            {
                library.IsChecked = true;
            }
        }
    }

    private AdminInvitationDraft Draft() => new()
    {
        Delivery = IsEmail ? InvitationDeliveries.Email : InvitationDeliveries.Link,
        Email = IsEmail ? Email.Trim() : null,
        Role = IsAdminRole ? AdminRoles.Admin : AdminRoles.User,
        AccessGroupId = !IsAdminRole && SelectedGroup is { Value: not AdminGroupFilter.None } group ? group.Value : null,
        LibraryIds = InheritLibraries ? null : [.. Libraries.Where(library => library.IsChecked).Select(library => library.Id)],
        CreateProfile = CreateProfile,
        ShowTour = ShowTour,
        Note = Note.Trim() is { Length: > 0 } note ? note : null,
    };
}
