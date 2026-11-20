using System.Net;
using Granary.Core.Api.Admin;
using Granary.Core.Api.Auth;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>
/// Users and Invitations act only as the account and profile they were opened as: every call is bound to them, and once
/// another account acts on the server a write is refused unsent and worded as a refusal, the page left usable.
/// </summary>
public sealed class AdminAccountBindingTests
{
    private const string Claim = "https://silo.example/invite#tok3n";

    private static readonly AdminUserCapabilities Capabilities = new()
    {
        Available = true, OwnershipTransfer = true, PasswordResetLink = true, LoginSessions = true, DefaultProfile = true,
    };

    private static readonly AdminUser Ana = new() { Id = "3", Username = "ana", Email = "ana@example.com", Role = "user", Enabled = true, PasswordLogin = true };

    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();
    private readonly FakeConfirmation _confirmation = new();

    public AdminAccountBindingTests()
    {
        _silo.Acting = _session;
        _silo.Map("api/v2/libraries", """{ "items": [] }""");
        _silo.Map("api/v2/admin/users/3", request => request.Method == HttpMethod.Get
            ? AdminUserPayloads.Versioned(AdminUserPayloads.User())
            : BrowseFakeSilo.NoContent());
        _silo.Map("api/v2/admin/users/3/password-reset", _ => BrowseFakeSilo.Json($$"""
            { "delivery": "link", "delivery_status": "not_requested", "reset_url": "https://silo.example/reset#s3cret", "expires_at": "2026-10-28T12:30:00.000Z" }
            """, HttpStatusCode.Created));
        _silo.Map("api/v2/admin/users/3/transfer-ownership", _ => BrowseFakeSilo.NoContent());
        _silo.Map("api/v2/admin/users/3/login-sessions", request => request.Method == HttpMethod.Delete
            ? BrowseFakeSilo.Json("""{ "revoked": 1 }""")
            : BrowseFakeSilo.Json("""{ "items": [ { "id": "s1", "device_name": "TV", "ip_address": "203.0.113.7", "created_at": "2026-10-01T12:00:00.000Z", "expires_at": "2026-11-01T12:00:00.000Z", "last_seen_at": null, "current": false } ], "page": { "has_more": false } }"""));
        _silo.Map("api/v2/admin/users/3/login-sessions/s1", _ => BrowseFakeSilo.NoContent());
        _silo.Map("api/v2/admin/invitations/capabilities", """{ "state": "available", "default_profile": true, "email_delivery": true }""");
        _silo.Map("api/v2/admin/settings/effective", """{ "server.public_url": "https://silo.example" }""");
        _silo.Map("api/v2/admin/invitations", request => request.Method == HttpMethod.Post
            ? Delivery()
            : BrowseFakeSilo.Json("""
                { "items": [ { "id": "1", "email": "ana@example.com", "delivery": "email_sent", "role": "user", "status": "pending",
                  "expires_at": "2026-11-03T12:00:00.000Z", "created_at": "2026-10-27T12:00:00.000Z" } ], "page": { "has_more": false } }
                """));
        _silo.Map("api/v2/admin/invitations/1/resend", _ => Delivery());
        _silo.Map("api/v2/admin/invitations/1", _ => BrowseFakeSilo.NoContent());
    }

    private static HttpResponseMessage Delivery() => BrowseFakeSilo.Json($$"""
        { "invitation": { "id": "1", "email": "", "status": "pending" }, "claim_url": "{{Claim}}", "delivery_status": "not_requested" }
        """, HttpStatusCode.Created);

    // Elena signs out and Kim, another admin, signs in on the same server with a profile of the same id.
    private void AnotherAccountSignsIn() => _session.Account = new Account("2", "kim", "kim@example.com", "admin", [], true, false, null);

    private void AssertEveryCallBound() =>
        Assert.All(_silo.Requests.Where(request => request.Uri.AbsolutePath.Contains("/admin/", StringComparison.Ordinal)),
            request => Assert.Equal(("1", "7"), (request.AsAccount, request.AsProfile)));

    private int Writes() => _silo.Requests.Count(request => request.Method != HttpMethod.Get);

    private AdminUserDetailViewModel Detail() => new(_session, new AdminUsersApi(_silo.Http), new AdminApi(_silo.Http), _confirmation,
        new ManualTimeProvider(AdminUserPayloads.Now), _silo.Logs.For<AdminUserDetailViewModel>());

    private AdminInvitationsViewModel Invitations() =>
        new(_session, new AdminInvitationsApi(_silo.Http), _confirmation, _silo.Logs.For<AdminInvitationsViewModel>());

    [Fact]
    public async Task EveryCallOfUsers_GoesAsTheSignedInAccountAndProfile()
    {
        var list = new AdminUsersViewModel(_session, new AdminUsersApi(_silo.Http), new ManualTimeProvider(AdminUserPayloads.Now),
            _silo.Logs.For<AdminUsersViewModel>());
        await list.LoadAsync();
        _silo.Map("api/v2/admin/users", request => request.Method == HttpMethod.Post
            ? BrowseFakeSilo.Json("""{ "id": "9" }""", HttpStatusCode.Created)
            : BrowseFakeSilo.Json("""{ "items": [], "page": { "has_more": false } }"""));
        var form = new AdminNewUserViewModel(_session, new AdminUsersApi(_silo.Http), _silo.Logs.For<AdminNewUserViewModel>());
        form.Start(Capabilities, [], groupsKnown: false, viewerIsOwner: true);
        (form.Username, form.Email, form.Password) = ("bo", "bo@example.com", "margin fossil");
        Assert.True(await form.SaveAsync(TestContext.Current.CancellationToken));

        var detail = Detail();
        await detail.OpenAsync("3", Capabilities, [], viewerIsOwner: true);
        detail.Username = "ana2";
        await detail.SaveSignInCommand.ExecuteAsync(null);
        await detail.DisableCommand.ExecuteAsync(null);
        await detail.MakeOwnerCommand.ExecuteAsync(null);
        var reset = new AdminPasswordResetViewModel(_session, new AdminUsersApi(_silo.Http), _silo.Logs.For<AdminPasswordResetViewModel>());
        reset.Start(Ana, Capabilities, passwordOnly: false);
        await reset.RunAsync(TestContext.Current.CancellationToken);
        var sessions = new AdminUserSessionsViewModel(_session, new AdminUsersApi(_silo.Http), _confirmation,
            new ManualTimeProvider(AdminUserPayloads.Now), _silo.Logs.For<AdminUserSessionsViewModel>());
        await sessions.OpenAsync(Ana, manageable: true);
        await sessions.SignOutCommand.ExecuteAsync(sessions.Sessions[0]);
        await sessions.SignOutEverywhereCommand.ExecuteAsync(null);
        var delete = new AdminUserDeleteViewModel(_session, new AdminUsersApi(_silo.Http), _silo.Logs.For<AdminUserDeleteViewModel>());
        Assert.True(await delete.StartAsync("3", TestContext.Current.CancellationToken));
        delete.Typed = "ana";
        Assert.True(await delete.DeleteAsync(TestContext.Current.CancellationToken));

        Assert.Equal(8, Writes());
        AssertEveryCallBound();
    }

    [Fact]
    public async Task EveryCallOfInvitations_GoesAsTheSignedInAccountAndProfile()
    {
        var tab = Invitations();
        await tab.OpenAsync(viewerIsOwner: true);
        Assert.True(await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Link));
        await tab.RevokeCommand.ExecuteAsync(tab.Rows[0]);
        var form = new AdminNewInvitationViewModel(_session, new AdminInvitationsApi(_silo.Http), new AdminApi(_silo.Http),
            _silo.Logs.For<AdminNewInvitationViewModel>());
        await form.StartAsync(tab.Capabilities, [], viewerIsOwner: true);
        form.IsEmail = false;
        Assert.True(await form.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, Writes());
        AssertEveryCallBound();
    }

    [Fact]
    public async Task AfterASwitch_AnAccountsChange_IsNotSent_AndSaysSo()
    {
        var detail = Detail();
        await detail.OpenAsync("3", Capabilities, [], viewerIsOwner: true);
        AnotherAccountSignsIn();

        detail.Username = "ana2";
        await detail.SaveSignInCommand.ExecuteAsync(null);
        await detail.DisableCommand.ExecuteAsync(null);

        Assert.Equal(0, Writes());
        Assert.False(detail.SignInCard.IsSaving);
        Assert.Equal(LibraryErrors.Denied, detail.SignInCard.Error);
        Assert.Equal(LibraryErrors.Denied, detail.Notice);
    }

    [Fact]
    public async Task AfterASwitch_DeleteAndReset_AreNotSent()
    {
        var delete = new AdminUserDeleteViewModel(_session, new AdminUsersApi(_silo.Http), _silo.Logs.For<AdminUserDeleteViewModel>());
        Assert.True(await delete.StartAsync("3", TestContext.Current.CancellationToken));
        var reset = new AdminPasswordResetViewModel(_session, new AdminUsersApi(_silo.Http), _silo.Logs.For<AdminPasswordResetViewModel>());
        reset.Start(Ana, Capabilities, passwordOnly: false);
        AnotherAccountSignsIn();

        delete.Typed = "ana";
        Assert.False(await delete.DeleteAsync(TestContext.Current.CancellationToken));
        await reset.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, Writes());
        Assert.Equal((false, LibraryErrors.Denied), (delete.IsBusy, delete.Error));
        Assert.Equal(LibraryErrors.Denied, reset.Error);
        Assert.Equal("", reset.ResetUrl);
    }

    [Fact]
    public async Task AfterASwitch_NoInvitationIsMadeResentOrRevoked()
    {
        var tab = Invitations();
        await tab.OpenAsync(viewerIsOwner: true);
        var form = new AdminNewInvitationViewModel(_session, new AdminInvitationsApi(_silo.Http), new AdminApi(_silo.Http),
            _silo.Logs.For<AdminNewInvitationViewModel>());
        await form.StartAsync(tab.Capabilities, [], viewerIsOwner: true);
        form.IsEmail = false;
        AnotherAccountSignsIn();

        await tab.RevokeCommand.ExecuteAsync(tab.Rows[0]);
        Assert.Equal((false, LibraryErrors.Denied), (tab.IsBlocked, tab.ActionError));
        Assert.False(await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Link));
        Assert.False(await form.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, Writes());
        Assert.StartsWith(LibraryErrors.Denied, tab.ActionError, StringComparison.Ordinal);
        Assert.Null(tab.Resent);
        Assert.Equal((false, LibraryErrors.Denied), (form.IsSaving, form.Error));
        Assert.Equal("", form.ClaimUrl);
    }
}
