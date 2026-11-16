using System.Net;
using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Reset password: an emailed link, a link to share shown once, or a temporary password.</summary>
public sealed class AdminPasswordResetViewModelTests
{
    private const string Secret = "https://silo.example/reset-password#3f2b47eb7b36dd2d";

    private static readonly AdminUser Ana = new() { Id = "3", Username = "ana", Email = "ana@example.com", Role = "user", Enabled = true, PasswordLogin = true };

    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();

    private AdminPasswordResetViewModel Started(AdminUserCapabilities capabilities, AdminUser? account = null, bool passwordOnly = false)
    {
        var dialog = new AdminPasswordResetViewModel(_session, new AdminUsersApi(_silo.Http), _silo.Logs.For<AdminPasswordResetViewModel>());
        dialog.Start(account ?? Ana, capabilities, passwordOnly);
        return dialog;
    }

    [Fact]
    public async Task ALink_IsShownOnce_WithWhomToShareIt()
    {
        _silo.Map("api/v2/admin/users/3/password-reset", _ => BrowseFakeSilo.Json($$"""
            { "delivery": "link", "delivery_status": "not_requested", "reset_url": "{{Secret}}", "expires_at": "2026-10-28T12:30:00.000Z" }
            """, HttpStatusCode.Created));
        var dialog = Started(new AdminUserCapabilities { PasswordResetLink = true });

        Assert.Equal(AdminResetMethod.Link, dialog.Method);
        Assert.False(dialog.CanEmail);
        Assert.Equal("Set up email in Settings to send reset links.", dialog.EmailHint);
        await dialog.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("""{"delivery":"link"}""", _silo.To("api/v2/admin/users/3/password-reset").Single().Body);
        Assert.Equal(Secret, dialog.ResetUrl);
        Assert.StartsWith("Share this link only with ana.", dialog.LinkShareText, StringComparison.Ordinal);
        Assert.True(dialog.IsDone);
        Assert.DoesNotContain("3f2b47eb7b36dd2d", _silo.Logs.All, StringComparison.Ordinal);

        dialog.Clear();
        Assert.Equal("", dialog.ResetUrl);
    }

    [Fact]
    public async Task AnUnconfirmedEmail_LeavesTheChoiceOpen()
    {
        _silo.Map("api/v2/admin/users/3/password-reset", _ => BrowseFakeSilo.Json("""
            { "delivery": "email", "delivery_status": "failed_or_unknown", "expires_at": "2026-10-28T12:30:00.000Z" }
            """, HttpStatusCode.Created));
        var dialog = Started(new AdminUserCapabilities { PasswordResetEmail = true, PasswordResetLink = true });

        Assert.Equal(AdminResetMethod.Email, dialog.Method);
        await dialog.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("The mail server did not confirm delivery. Send it again, or create a link to share.", dialog.Outcome);
        Assert.False(dialog.IsDone);
        Assert.True(dialog.CanChoose);
    }

    [Fact]
    public async Task ATemporaryPassword_IsSentAtTheFreshVersion_AndForgotten()
    {
        string? ifMatch = null;
        _silo.Map("api/v2/admin/users/3", request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return AdminUserPayloads.Versioned(AdminUserPayloads.User(), "\"v7\"");
            }
            ifMatch = request.Headers.GetValues("If-Match").Single();
            return BrowseFakeSilo.NoContent();
        });
        // A disabled account gets no link; a temporary password still works.
        var dialog = Started(new AdminUserCapabilities { PasswordResetLink = true }, Ana with { Enabled = false });

        Assert.Equal(AdminResetMethod.Password, dialog.Method);
        dialog.Password = "short";
        Assert.False(dialog.CanRun);
        dialog.Password = "margin fossil";
        await dialog.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("\"v7\"", ifMatch);
        Assert.Equal("""{"password":"margin fossil","require_password_change":true}""", _silo.To("api/v2/admin/users/3").Last().Body);
        Assert.Equal("Password set. ana is signed out everywhere. They must choose a new one at next sign-in.", dialog.Outcome);
        Assert.Equal("", dialog.Password);
        Assert.DoesNotContain("margin fossil", _silo.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public void SetPassword_ForAnExternalAccount_OffersOnlyThePassword()
    {
        var dialog = Started(new AdminUserCapabilities { PasswordResetEmail = true, PasswordResetLink = true }, passwordOnly: true);

        Assert.False(dialog.CanEmail);
        Assert.False(dialog.CanLink);
        Assert.Equal(AdminResetMethod.Password, dialog.Method);
    }

    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("ééééééééééééééééééééééééééééééééééééé", false)]
    public void APassword_FitsByCharactersAndBytes(string password, bool fits)
    {
        Assert.Equal(fits, AdminPasswordResetViewModel.Fits(password));
    }
}
