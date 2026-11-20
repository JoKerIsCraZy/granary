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
        _silo.Map("api/v2/admin/settings/effective", """{ "server.public_url": "https://silo.example", "smtp.password": "" }""");
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
        Assert.True(tab.IsBlocked);
        await tab.RestartCommand.ExecuteAsync(null);
        Assert.False(tab.IsBlocked);
        Assert.Equal(2, _silo.To("api/v2/admin/invitations").Count(r => r.Method == HttpMethod.Get));
        Assert.DoesNotContain("tok3n", tab.Resent!.ToString(), StringComparison.Ordinal);
        tab.ForgetResent();
        Assert.Null(tab.Resent);
    }

    [Fact]
    public async Task AResendWithoutAnAnswer_BlocksTheRows_UntilTheHistoryIsReadAgain()
    {
        _silo.Map("api/v2/admin/invitations/1/resend", _ => throw new HttpRequestException("no answer"));
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
        var form = new AdminNewInvitationViewModel(new AdminInvitationsApi(_silo.Http), new AdminApi(_silo.Http),
            _silo.Logs.For<AdminNewInvitationViewModel>());
        AdminAccessGroup[] groups = [new() { Id = "10", Name = "Family", IsDefault = true }, new() { Id = "11", Name = "Friends" }];
        await form.StartAsync(AdminActors.Of(_session), new AdminInvitationCapabilities { State = "available", DefaultProfile = true }, groups, viewerIsOwner: true);

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
        var form = new AdminNewInvitationViewModel(new AdminInvitationsApi(_silo.Http), new AdminApi(_silo.Http),
            _silo.Logs.For<AdminNewInvitationViewModel>());
        await form.StartAsync(AdminActors.Of(_session), new AdminInvitationCapabilities { State = "available", DefaultProfile = true }, [], viewerIsOwner: false);

        Assert.False(await form.SaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Set the Silo public URL to create invitation links.", form.Error);
        Assert.True(form.NeedsReload);
        Assert.False(form.CanSubmit);
        form.ReloadDone();
        Assert.True(form.CanSubmit);
    }
    [Fact]
    public async Task AMissingPublicUrl_StopsInvitesAndNewLinks_ButNotRevoke()
    {
        _silo.Map("api/v2/admin/settings/effective", """{ "server.public_url": "  " }""");
        var tab = await OpenedAsync();

        Assert.True(tab.PublicUrlMissing);
        Assert.False(tab.CanInvite);
        Assert.Equal("Set the Silo public URL to create invitation links.", tab.PublicUrlMissingText);
        Assert.True(tab.Rows[0].CanResendLink);
        Assert.False(tab.Rows[0].ResendEnabled);
        Assert.True(tab.Rows[0].CanRevoke);
    }

    [Fact]
    public async Task UnreadableSettings_BlockNothing()
    {
        _silo.Map("api/v2/admin/settings/effective", _ => BrowseFakeSilo.Problem(403, "forbidden"));
        var tab = await OpenedAsync();

        Assert.False(tab.PublicUrlMissing);
        Assert.True(tab.CanInvite);
        Assert.True(tab.Rows[0].ResendEnabled);
    }

    [Fact]
    public async Task FailedCapabilities_ReadNoHistory_AndCanBeReadAgain()
    {
        _silo.Map("api/v2/admin/invitations/capabilities", _ => BrowseFakeSilo.Problem(503, "dependency_unavailable"));
        var tab = await OpenedAsync();

        Assert.Equal(LibraryLoadState.Error, tab.State);
        Assert.Equal("Could not load invitation capabilities.", tab.CapabilitiesFailedText);
        Assert.Empty(_silo.To("api/v2/admin/invitations"));

        _silo.Map("api/v2/admin/invitations/capabilities", Capabilities);
        await tab.ReloadCapabilitiesCommand.ExecuteAsync(null);
        Assert.Equal(LibraryLoadState.Ready, tab.State);
        Assert.Equal(2, tab.Rows.Count);
    }

    [Fact]
    public async Task APageThatArrivesAfterLeaving_IsDropped()
    {
        var page2 = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.Map("api/v2/admin/invitations", (request, _) => request.RequestUri!.Query.Contains("cursor=i2")
            ? page2.Task
            : Task.FromResult(BrowseFakeSilo.Json(Page1)));
        var tab = await OpenedAsync();

        var more = tab.LoadMoreCommand.ExecuteAsync(null);
        Assert.False(tab.LoadMoreCommand.CanExecute(null));
        tab.Deactivate();
        page2.SetResult(BrowseFakeSilo.Json(Page2));
        await more;

        Assert.Equal(2, tab.Rows.Count);
        Assert.False(tab.IsHistoryLoading);
    }

    [Fact]
    public async Task AReloadWhileMoreLoads_DropsTheOlderPage()
    {
        var page2 = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.Map("api/v2/admin/invitations", (request, _) => request.RequestUri!.Query.Contains("cursor=i2")
            ? page2.Task
            : Task.FromResult(BrowseFakeSilo.Json(Page1)));
        var tab = await OpenedAsync();

        var more = tab.LoadMoreCommand.ExecuteAsync(null);
        await tab.RestartCommand.ExecuteAsync(null);
        page2.SetResult(BrowseFakeSilo.Json(Page2));
        await more;

        Assert.Equal(["1", "2"], tab.Rows.Select(row => row.Id));
        Assert.True(tab.HasMore);
    }

    [Fact]
    public async Task Leaving_ForgetsTheNewLink()
    {
        _silo.Map("api/v2/admin/invitations/1/resend", _ => Created("1", "not_requested", "ana@example.com"));
        var tab = await OpenedAsync();
        Assert.True(await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Link));

        tab.Deactivate();

        Assert.Null(tab.Resent);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ARefusedOrGoneRevoke_IsWordedByItsStatus(int status)
    {
        string text = status == 403 ? LibraryErrors.Denied : LibraryErrors.Gone;
        _silo.Map("api/v2/admin/invitations/1", _ => BrowseFakeSilo.Problem(status, status == 403 ? "forbidden" : "not_found"));
        var tab = await OpenedAsync();

        await tab.RevokeCommand.ExecuteAsync(tab.Rows[0]);

        Assert.Equal(text, tab.ActionError);
        Assert.False(tab.IsBlocked);
        Assert.DoesNotContain("Silo internal detail", tab.ActionError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ARefusedOrGoneResend_IsWordedByItsStatus(int status)
    {
        string text = status == 403 ? LibraryErrors.Denied : LibraryErrors.Gone;
        _silo.Map("api/v2/admin/invitations/1/resend", _ => BrowseFakeSilo.Problem(status, status == 403 ? "forbidden" : "not_found"));
        var tab = await OpenedAsync();

        Assert.False(await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Link));

        Assert.StartsWith(text, tab.ActionError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ARefusedInvite_IsWordedByItsStatus(int status)
    {
        string text = status == 403 ? LibraryErrors.Denied : LibraryErrors.Gone;
        _silo.Map("api/v2/admin/invitations", _ => BrowseFakeSilo.Problem(status, status == 403 ? "forbidden" : "not_found"));
        var form = new AdminNewInvitationViewModel(new AdminInvitationsApi(_silo.Http), new AdminApi(_silo.Http),
            _silo.Logs.For<AdminNewInvitationViewModel>());
        await form.StartAsync(AdminActors.Of(_session), new AdminInvitationCapabilities { State = "available", DefaultProfile = true }, [], viewerIsOwner: true);

        Assert.False(await form.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal(text, form.Error);
    }

    [Fact]
    public async Task NothingSecret_IsLogged()
    {
        _silo.Map("api/v2/admin/invitations/1/resend", _ => Created("1", "sent", "ana@example.com"));
        var tab = await OpenedAsync();
        await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Email);
        _silo.Map("api/v2/admin/invitations/1/resend", _ => BrowseFakeSilo.Problem(500, "internal_error"));
        await tab.RestartCommand.ExecuteAsync(null);
        await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Email);

        string logs = _silo.Logs.All;
        Assert.DoesNotContain("tok3n", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@example.com", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("elena", logs, StringComparison.Ordinal);
    }
    [Fact]
    public async Task ARevokeWithoutAnAnswer_HoldsTheRows_UntilTheHistoryIsReadAgain()
    {
        _silo.Map("api/v2/admin/invitations/1", _ => throw new HttpRequestException("no answer"));
        var tab = await OpenedAsync();

        await tab.RevokeCommand.ExecuteAsync(tab.Rows[0]);

        Assert.True(tab.IsBlocked);
        await tab.RestartCommand.ExecuteAsync(null);
        Assert.False(tab.IsBlocked);
    }

    [Fact]
    public async Task ANewLinkThatArrivesAfterLeaving_IsNotShown_AlsoWhenTheTabOpensAgain()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.Map("api/v2/admin/invitations/1/resend", (_, _) => answer.Task);
        var tab = await OpenedAsync();

        var resend = tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Link);
        tab.Deactivate();
        await tab.OpenAsync(viewerIsOwner: true);
        answer.SetResult(Created("1", "not_requested", ""));

        Assert.False(await resend);
        Assert.Equal(("", ""), (tab.ResentLink, tab.ResentMessage));
    }
    [Fact]
    public async Task ARevokeThatEndsAfterLeaving_LeavesTheNextVisitAlone()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.Map("api/v2/admin/invitations/1", (_, _) => answer.Task);
        var tab = await OpenedAsync();

        var revoke = tab.RevokeCommand.ExecuteAsync(tab.Rows[0]);
        tab.Deactivate();
        await tab.OpenAsync(viewerIsOwner: true);
        answer.SetResult(BrowseFakeSilo.Problem(403, "forbidden"));
        await revoke;

        Assert.Equal("", tab.ActionError);
        Assert.False(tab.IsBlocked);
    }
    [Fact]
    public async Task AResendTheServerRefused_FreesTheRows_WithoutTheUncertainNote()
    {
        _silo.Map("api/v2/admin/invitations/1/resend", _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var tab = await OpenedAsync();

        Assert.False(await tab.ResendAsync(tab.Rows[0], InvitationDeliveries.Email));

        Assert.False(tab.IsBlocked);
        Assert.DoesNotContain("Reload history before another deliberate resend.", tab.ActionError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InviteSomeoneStartedTwice_ListsTheLibrariesOnce()
    {
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        _silo.Map("api/v2/libraries", (_, _) => ++reads == 1
            ? first.Task
            : Task.FromResult(BrowseFakeSilo.Json("""{ "items": [ { "id": "5", "name": "Films" }, { "id": "6", "name": "Shows" } ] }""")));
        var form = new AdminNewInvitationViewModel(new AdminInvitationsApi(_silo.Http), new AdminApi(_silo.Http),
            _silo.Logs.For<AdminNewInvitationViewModel>());
        var capabilities = new AdminInvitationCapabilities { State = "available", DefaultProfile = true };

        var older = form.StartAsync(AdminActors.Of(_session), capabilities, [], viewerIsOwner: true);
        await form.StartAsync(AdminActors.Of(_session), capabilities, [], viewerIsOwner: true);
        first.SetResult(BrowseFakeSilo.Json("""{ "items": [ { "id": "5", "name": "Films" }, { "id": "6", "name": "Shows" } ] }"""));
        await older;

        Assert.Equal(["5", "6"], form.Libraries.Select(library => library.Id));
    }
}
