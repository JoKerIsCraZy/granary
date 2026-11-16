using System.Net;
using System.Text.Json;
using Granary.Core.Api.Admin;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Admin → Users → Invitations: the history, a new link shown once, Revoke, and Invite someone.</summary>
public sealed class AdminInvitationsViewModelTests
{
    private const string Claim = "https://silo.example/invite#tok3n-s3cret";

    private const string Capabilities = """{ "state": "available", "default_profile": true, "profileless": true, "email_delivery": true }""";

    // A pending email invitation from Elena, an accepted link invitation with its note, and an admin's expired one.
    private const string Page1 = """
        { "items": [
            { "id": "1", "email": "ana@example.com", "delivery": "email_sent", "role": "user", "library_ids": null, "create_profile": true,
              "show_tour": true, "note": "", "invited_by": "1", "invited_by_name": "elena", "status": "pending",
              "expires_at": "2026-11-03T12:00:00.000Z", "created_at": "2026-10-27T12:00:00.000Z" },
            { "id": "2", "email": "bo@example.com", "delivery": "link", "role": "user", "library_ids": null, "create_profile": true,
              "show_tour": true, "note": "For Bo", "invited_by": "1", "invited_by_name": "elena", "status": "accepted",
              "expires_at": "2026-11-01T12:00:00.000Z", "created_at": "2026-10-25T12:00:00.000Z" }
          ], "page": { "has_more": true, "next_cursor": "i2" } }
        """;

    private const string Page2 = """
        { "items": [
            { "id": "3", "email": "", "delivery": "link", "role": "admin", "library_ids": null, "create_profile": false, "show_tour": true,
              "note": "", "invited_by": "1", "invited_by_name": "", "status": "expired", "expires_at": "2026-10-20T12:00:00.000Z",
              "created_at": "2026-10-13T12:00:00.000Z" }
          ], "page": { "has_more": false } }
        """;

    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();
    private readonly FakeConfirmation _confirmation = new();

    public AdminInvitationsViewModelTests()
    {
        _silo.Map("api/v2/admin/invitations/capabilities", Capabilities);
        _silo.Map("api/v2/admin/invitations", request => request.Method == HttpMethod.Post
            ? Created("9", "not_requested", "")
            : BrowseFakeSilo.Json(request.RequestUri!.Query.Contains("cursor=i2") ? Page2 : Page1));
        _silo.Map("api/v2/libraries", """{ "items": [ { "id": "5", "name": "Films" }, { "id": "6", "name": "Shows" } ] }""");
    }

    private static HttpResponseMessage Created(string id, string status, string email) => BrowseFakeSilo.Json($$"""
        { "invitation": { "id": "{{id}}", "email": "{{email}}", "delivery": "link", "role": "user", "status": "pending",
          "expires_at": "2026-11-03T12:00:00.000Z", "created_at": "2026-10-27T12:00:00.000Z" },
          "claim_url": "{{Claim}}", "delivery_status": "{{status}}" }
        """, HttpStatusCode.Created);

    private async Task<AdminInvitationsViewModel> OpenedAsync(bool owner = true)
    {
        var tab = new AdminInvitationsViewModel(_session, new AdminInvitationsApi(_silo.Http), _confirmation, _silo.Logs.For<AdminInvitationsViewModel>());
        await tab.OpenAsync(owner);
        return tab;
    }

    [Fact]
    public async Task Open_ListsTheHistory_AsTheWebsRows()
    {
        var tab = await OpenedAsync();

        Assert.Equal(LibraryLoadState.Ready, tab.State);
        var pending = tab.Rows[0];
        Assert.Equal(("ana@example.com", "Invited by elena", "User", "Pending", "Email sent"),
            (pending.Recipient, pending.Details, pending.RoleText, pending.StatusText, pending.DeliveryText));
        Assert.True(pending.CanResendLink);
        Assert.True(pending.CanResendEmail);
        Assert.True(pending.CanRevoke);
        Assert.Equal("Copy a new link without emailing", pending.ResendLinkText);
        var accepted = tab.Rows[1];
        Assert.Equal("Joined by link · “For Bo” · Invited by elena", accepted.Details);
        Assert.False(accepted.CanResendLink);
        Assert.False(accepted.CanRevoke);

        await tab.LoadMoreCommand.ExecuteAsync(null);
        var expired = tab.Rows[2];
        Assert.Equal(("Link invitation", "Replace with a new link"), (expired.Recipient, expired.ResendLinkText));
        Assert.True(expired.CanResendLink);
        Assert.False(tab.HasMore);
        Assert.Equal("50", _silo.To("api/v2/admin/invitations")[0].Query("limit"));
    }

    [Fact]
    public async Task OnlyTheOwner_ResendsAnAdminsInvitation()
    {
        var tab = await OpenedAsync(owner: false);
        await tab.LoadMoreCommand.ExecuteAsync(null);

        Assert.False(tab.Rows[2].CanResendLink);
    }

    [Fact]
    public async Task Resend_ShowsTheNewLinkOnce_AndReadsTheHistoryAgain()
    {
        _silo.Map("api/v2/admin/invitations/1/resend", _ => Created("1", "not_requested", "ana@example.com"));
        var tab = await OpenedAsync();

        Assert.True(await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Link));

        Assert.Equal("""{"delivery":"link"}""", _silo.To("api/v2/admin/invitations/1/resend").Single().Body);
        Assert.Equal(Claim, tab.Resent?.ClaimUrl);
        Assert.Equal("New link created for ana@example.com. Nothing was emailed; share this link with them yourself.",
            AdminInvitationRow.DeliveryMessage(tab.Resent!));
        Assert.Equal(2, _silo.To("api/v2/admin/invitations").Count(r => r.Method == HttpMethod.Get));
        Assert.DoesNotContain("tok3n", tab.Resent!.ToString(), StringComparison.Ordinal);
        tab.ForgetResent();
        Assert.Null(tab.Resent);
    }

    [Fact]
    public async Task AFailedResend_BlocksTheRows_UntilTheHistoryIsReadAgain()
    {
        _silo.Map("api/v2/admin/invitations/1/resend", _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var tab = await OpenedAsync();

        Assert.False(await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Email));
        Assert.True(tab.IsBlocked);
        Assert.EndsWith("Reload history before another deliberate resend.", tab.ActionError, StringComparison.Ordinal);

        await tab.RestartCommand.ExecuteAsync(null);
        Assert.False(tab.IsBlocked);
    }

    [Fact]
    public async Task Revoke_AsksFirst_WithTheWebsWords()
    {
        _silo.Map("api/v2/admin/invitations/1", _ => BrowseFakeSilo.NoContent());
        var tab = await OpenedAsync();

        await tab.RevokeCommand.ExecuteAsync(tab.Rows[0]);

        Assert.Equal(("Revoke invitation", "Revoke the invitation for ana@example.com? Their link will stop working.", "Revoke"),
            _confirmation.Asked.Single());
        Assert.Equal(HttpMethod.Delete, _silo.To("api/v2/admin/invitations/1").Single().Method);
    }

    [Fact]
    public async Task NotConfigured_IsUnavailable()
    {
        _silo.Map("api/v2/admin/invitations/capabilities", """{ "state": "not_configured", "default_profile": false, "email_delivery": false }""");
        var tab = await OpenedAsync();

        Assert.Equal(LibraryLoadState.Unavailable, tab.State);
        Assert.Empty(_silo.To("api/v2/admin/invitations"));
    }

    [Fact]
    public async Task InviteSomeone_SendsALinkInvitation_AndShowsItsLinkOnce()
    {
        var form = new AdminNewInvitationViewModel(_session, new AdminInvitationsApi(_silo.Http), new AdminApi(_silo.Http),
            _silo.Logs.For<AdminNewInvitationViewModel>());
        AdminAccessGroup[] groups = [new() { Id = "10", Name = "Family", IsDefault = true }, new() { Id = "11", Name = "Friends" }];
        await form.StartAsync(new AdminInvitationCapabilities { State = "available", DefaultProfile = true }, groups, viewerIsOwner: true);

        Assert.False(form.IsEmail);
        Assert.Equal(["Family (default)", "Friends"], form.GroupChoices.Select(choice => choice.Name));
        form.SelectedGroup = form.GroupChoices[1];
        form.InheritLibraries = false;
        form.Libraries[0].IsChecked = false;
        form.Note = "  For the family  ";
        Assert.True(await form.SaveAsync(TestContext.Current.CancellationToken));

        var body = JsonDocument.Parse(_silo.To("api/v2/admin/invitations").Single(r => r.Method == HttpMethod.Post).Body!).RootElement;
        Assert.Equal("""{"delivery":"link","role":"user","access_group_id":"11","library_ids":["6"],"create_profile":true,"show_tour":true,"note":"For the family"}""",
            body.GetRawText());
        Assert.Equal(Claim, form.ClaimUrl);
        Assert.Equal("Invitation link created. Share it with the person you're inviting. They'll enter their email address and choose a password.",
            form.ResultMessage);
        form.Clear();
        Assert.Equal("", form.ClaimUrl);
    }

    [Fact]
    public async Task AFailedInvite_WaitsForTheHistory_BeforeAnotherTry()
    {
        _silo.Map("api/v2/admin/invitations", _ => BrowseFakeSilo.Problem(409, "capability_not_configured"));
        var form = new AdminNewInvitationViewModel(_session, new AdminInvitationsApi(_silo.Http), new AdminApi(_silo.Http),
            _silo.Logs.For<AdminNewInvitationViewModel>());
        await form.StartAsync(new AdminInvitationCapabilities { State = "available", DefaultProfile = true }, [], viewerIsOwner: false);

        Assert.False(await form.SaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Set the Silo public URL to create invitation links.", form.Error);
        Assert.True(form.NeedsReload);
        Assert.False(form.CanSubmit);
        form.ReloadDone();
        Assert.True(form.CanSubmit);
    }
}
