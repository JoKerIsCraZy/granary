using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels.Admin;

/// <summary>How a locked-out account gets back in, as the web's password reset dialog offers it.</summary>
public enum AdminResetMethod
{
    Email,
    Link,
    Password,
}

/// <summary>
/// The web's password reset dialog (<c>components/AdminUserPasswordResetDialog.tsx</c>): email the account a reset link,
/// create one to share, or set a temporary password now; each signs it out everywhere. A link to share is a bearer
/// credential: it is shown once to the administrator who made it, copied on request, and kept nowhere else; so is the
/// temporary password, which is sent once and then cleared.
/// </summary>
public sealed partial class AdminPasswordResetViewModel(ISessionManager session, AdminUsersApi users, ILogger<AdminPasswordResetViewModel> logger)
    : ObservableObject
{
    /// <summary>The web's MIN_PASSWORD_CHARS and MAX_PASSWORD_BYTES.</summary>
    public const int MinPasswordChars = 8;
    public const int MaxPasswordBytes = 72;

    private AdminUser _account = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(ActionText), nameof(IsEmail), nameof(IsLink), nameof(IsPassword))]
    public partial AdminResetMethod Method { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool MustChange { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanChoose))]
    public partial bool IsBusy { get; private set; }

    /// <summary>A done reset: the dialog only says what happened and offers Done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    public partial bool IsDone { get; private set; }

    [ObservableProperty]
    public partial string Error { get; private set; } = "";

    /// <summary>What happened: the email sent, the password set, or the email not confirmed.</summary>
    [ObservableProperty]
    public partial string Outcome { get; private set; } = "";

    /// <summary>The link to share, shown once; empty otherwise.</summary>
    [ObservableProperty]
    public partial string ResetUrl { get; private set; } = "";

    [ObservableProperty]
    public partial string LinkShareText { get; private set; } = "";

    public bool CanEmail { get; private set; }

    public bool CanLink { get; private set; }

    public string Title => AppText.Get("web.admin.users.passwordResetDialog.title", ("username", _account.Username));

    public string EmailHint { get; private set; } = "";

    public string LinkHint { get; private set; } = "";

    public string PasswordRules => AppText.Get("web.admin.users.passwordResetDialog.passwordRules", ("min", MinPasswordChars), ("max", MaxPasswordBytes));

    public bool IsEmail
    {
        get => Method == AdminResetMethod.Email;
        set => Pick(value, AdminResetMethod.Email);
    }

    public bool IsLink
    {
        get => Method == AdminResetMethod.Link;
        set => Pick(value, AdminResetMethod.Link);
    }

    public bool IsPassword
    {
        get => Method == AdminResetMethod.Password;
        set => Pick(value, AdminResetMethod.Password);
    }

    public bool CanChoose => !IsBusy && !IsDone;

    public bool CanRun => !IsBusy && !IsDone && Method switch
    {
        AdminResetMethod.Email => CanEmail,
        AdminResetMethod.Link => CanLink,
        _ => Fits(Password),
    };

    public string ActionText => AppText.Get(Method switch
    {
        AdminResetMethod.Email => "web.admin.users.passwordResetDialog.emailAction",
        AdminResetMethod.Link => "web.admin.users.passwordResetDialog.linkAction",
        _ => "web.admin.users.passwordResetDialog.passwordAction",
    });

    /// <summary>
    /// Starts the dialog for <paramref name="account"/>. The server refuses reset links for a disabled account, and for one
    /// a sign-in provider manages (<paramref name="passwordOnly"/>, the header's Set password): a temporary password still works.
    /// </summary>
    public void Start(AdminUser account, AdminUserCapabilities capabilities, bool passwordOnly)
    {
        _account = account;
        CanEmail = !passwordOnly && account.Enabled && capabilities.PasswordResetEmail && account.Email.Length > 0;
        CanLink = !passwordOnly && account.Enabled && capabilities.PasswordResetLink;
        EmailHint = CanEmail ? AppText.Get("web.admin.users.passwordResetDialog.emailHint", ("email", account.Email))
            : !account.Enabled ? AppText.Get("web.admin.users.passwordResetDialog.emailHintDisabled")
            : account.Email.Length == 0 ? AppText.Get("web.admin.users.passwordResetDialog.emailHintNoAddress")
            : AppText.Get("web.admin.users.passwordResetDialog.emailHintNoMail");
        LinkHint = CanLink ? AppText.Get("web.admin.users.passwordResetDialog.linkHint")
            : !account.Enabled ? AppText.Get("web.admin.users.passwordResetDialog.linkHintDisabled")
            : AdminText.WithoutLink("web.admin.users.passwordResetDialog.linkHintNoUrl");
        Password = Error = Outcome = ResetUrl = LinkShareText = "";
        MustChange = true;
        IsDone = false;
        Method = CanEmail ? AdminResetMethod.Email : CanLink ? AdminResetMethod.Link : AdminResetMethod.Password;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Runs the chosen way; the dialog stays open to show what happened.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRun || session.ActiveServer is not { } server)
        {
            return;
        }
        IsBusy = true;
        Error = "";
        try
        {
            if (Method == AdminResetMethod.Password)
            {
                await SetPasswordAsync(server.BaseUri, cancellationToken);
            }
            else
            {
                Show(await users.IssuePasswordResetAsync(server.BaseUri, _account.Id,
                    Method == AdminResetMethod.Email ? AdminResetDelivery.Email : AdminResetDelivery.Link, cancellationToken));
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not reset an account's password");
            Error = AdminFailures.IsChanged(ex) ? AppText.Get("web.admin.users.page.accountChanged")
                : AdminFailures.Describe(ex, AppText.Get(Method == AdminResetMethod.Password
                    ? "web.admin.users.passwordResetDialog.setFailed"
                    : "web.admin.users.passwordResetDialog.issueFailed"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Forgets the link and the password when the dialog closes.</summary>
    public void Clear()
    {
        Password = ResetUrl = LinkShareText = "";
    }

    /// <summary>At least 8 characters and no more than 72 bytes of UTF-8, as the server takes a password.</summary>
    public static bool Fits(string password) =>
        password.EnumerateRunes().Count() >= MinPasswordChars && Encoding.UTF8.GetByteCount(password) <= MaxPasswordBytes;

    private void Pick(bool value, AdminResetMethod method)
    {
        if (value)
        {
            Method = method;
        }
    }

    // Read fresh, so the save carries the account's current version.
    private async Task SetPasswordAsync(Uri server, CancellationToken cancellationToken)
    {
        var fresh = await users.GetAsync(server, _account.Id, cancellationToken);
        await users.UpdateAsync(server, _account.Id, fresh.Version,
            new AdminUserChange { Password = Password, RequirePasswordChange = MustChange ? true : null }, cancellationToken);
        Password = "";
        Outcome = MustChange
            ? AppText.Get("web.admin.users.passwordResetDialog.passwordSetMustChange", ("username", _account.Username))
            : AppText.Get("web.admin.users.passwordResetDialog.passwordSet", ("username", _account.Username));
        IsDone = true;
    }

    private void Show(AdminPasswordReset result)
    {
        string expires = DateText.DateTime(result.ExpiresAt.LocalDateTime);
        bool unconfirmed = result.Delivery == AdminResetDelivery.Email && result.DeliveryStatus != "sent";
        Outcome = result.Delivery == AdminResetDelivery.Email
            ? unconfirmed ? AppText.Get("web.admin.users.passwordResetDialog.emailUnconfirmed")
                : AppText.Get("web.admin.users.passwordResetDialog.emailSent", ("email", _account.Email), ("expires", expires))
            : "";
        if (result.ResetUrl is { Length: > 0 } url)
        {
            ResetUrl = url;
            LinkShareText = AppText.Get("web.admin.users.passwordResetDialog.linkShare", ("username", _account.Username), ("expires", expires));
        }
        // An unconfirmed email leaves the administrator to send again or switch to a link.
        IsDone = !unconfirmed;
    }
}
