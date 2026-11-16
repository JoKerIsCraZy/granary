using System.Text.Json;
using Granary.Core.Api.Admin;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>An account's page: its header, its Sign-in and Library access cards, and the header's actions.</summary>
public sealed class AdminUserDetailViewModelTests
{
    private static readonly AdminAccessGroup[] Groups = [new() { Id = "10", Name = "Family", IsDefault = true }, new() { Id = "11", Name = "Friends" }];

    private static readonly AdminUserCapabilities Capabilities = new()
    {
        Available = true, OwnershipTransfer = true, PasswordResetLink = true, LoginSessions = true,
    };

    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();
    private readonly FakeConfirmation _confirmation = new();
    private readonly List<(HttpMethod Method, string? IfMatch, string? Body)> _writes = [];
    private string _account = AdminUserPayloads.User();
    private int _version = 1;
    private int _writeStatus = 204;

    public AdminUserDetailViewModelTests()
    {
        _silo.Map("api/v2/libraries", """{ "items": [ { "id": "5", "name": "Films", "sort_order": 1 }, { "id": "6", "name": "Shows", "sort_order": 2 } ] }""");
        _silo.Map("api/v2/admin/users/3", async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return AdminUserPayloads.Versioned(_account, $"\"v{_version}\"");
            }
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            _writes.Add((request.Method, request.Headers.TryGetValues("If-Match", out var tags) ? tags.Single() : null, body));
            return _writeStatus == 204 ? BrowseFakeSilo.NoContent() : BrowseFakeSilo.Problem(_writeStatus, "precondition_failed");
        });
    }

    private async Task<AdminUserDetailViewModel> OpenedAsync(bool viewerIsOwner = true)
    {
        var detail = new AdminUserDetailViewModel(_session, new AdminUsersApi(_silo.Http), new AdminApi(_silo.Http), _confirmation,
            new ManualTimeProvider(AdminUserPayloads.Now), _silo.Logs.For<AdminUserDetailViewModel>());
        await detail.OpenAsync("3", Capabilities, Groups, viewerIsOwner);
        return detail;
    }

    private JsonElement LastBody() => JsonDocument.Parse(_writes[^1].Body!).RootElement;

    [Fact]
    public async Task Open_ShowsTheHeader_AndTheActionsTheViewerHas()
    {
        var detail = await OpenedAsync();

        Assert.Equal(LibraryLoadState.Ready, detail.State);
        var header = detail.Header!;
        Assert.Equal(("ana", "user", "Active"), (header.Username, header.RoleText, header.StatusText));
        Assert.Equal("ana@example.com · Group: Family · Joined Mar 2, 2026 · No recorded activity · Password sign-in", header.Facts);
        Assert.True(header.ShowsReset);
        Assert.False(header.ShowsSetPassword);
        Assert.True(header.CanDisable);
        Assert.True(header.CanDelete);
        Assert.False(header.CanTransfer);
        Assert.False(header.IsViewOnly);
        Assert.Equal(["Films", "Shows"], detail.Libraries.Select(library => library.Name));
        Assert.Equal("Family", detail.SelectedGroup?.Name);
        Assert.True(detail.MarkerEdit);
        Assert.False(detail.HasSignInChanges);
        Assert.False(detail.HasAccessChanges);
    }

    [Fact]
    public async Task AnotherAdmin_IsViewOnly_ForAnAdminWhoIsNotTheOwner()
    {
        _account = AdminUserPayloads.User(role: "admin", group: "null");
        var detail = await OpenedAsync(viewerIsOwner: false);

        Assert.True(detail.Header!.IsViewOnly);
        Assert.Equal("Only the server owner can change another admin account. You can view it.", detail.Header.ViewOnlyNotice);
        Assert.False(detail.CanEditSignIn);
        Assert.False(detail.CanEditAccess);
        Assert.False(detail.Header.HasMenu);
    }

    [Fact]
    public async Task SaveSignIn_SendsOnlyWhatChanged_AtTheVersionRead()
    {
        var detail = await OpenedAsync();
        detail.Username = "ana2";
        detail.CanSignIn = false;
        Assert.True(detail.HasSignInChanges);

        await detail.SaveSignInCommand.ExecuteAsync(null);

        Assert.Equal((HttpMethod.Put, "\"v1\""), (_writes[0].Method, _writes[0].IfMatch));
        Assert.Equal("""{"username":"ana2","enabled":false}""", _writes[0].Body);
        Assert.Equal("Saved", detail.SignInCard.Saved);
    }

    [Fact]
    public async Task AChangeMeanwhile_KeepsTheEdits_AndReviewReadsTheNewVersion()
    {
        var detail = await OpenedAsync();
        detail.Email = "ana.new@example.com";
        _writeStatus = 412;

        await detail.SaveSignInCommand.ExecuteAsync(null);
        Assert.True(detail.SignInCard.HasConflict);
        Assert.Equal("Another admin changed this account after you started. Your edits are kept.", detail.SignInCard.Error);

        _version = 2;
        await detail.ReviewSignInCommand.ExecuteAsync(null);
        Assert.Equal("ana.new@example.com", detail.Email);
        Assert.False(detail.SignInCard.HasConflict);

        _writeStatus = 204;
        await detail.SaveSignInCommand.ExecuteAsync(null);
        Assert.Equal("\"v2\"", _writes[^1].IfMatch);
    }

    [Fact]
    public async Task AMalformedEmailOrNoName_IsNotSent()
    {
        var detail = await OpenedAsync();
        detail.Username = " ";

        await detail.SaveSignInCommand.ExecuteAsync(null);
        Assert.Equal("Enter a username.", detail.SignInCard.Error);
        detail.Username = "ana";
        detail.Email = "nope";
        await detail.SaveSignInCommand.ExecuteAsync(null);

        Assert.Equal("Enter a valid email address, like name@example.com", detail.SignInCard.Error);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task SaveAccess_SendsTheGroupLibrariesAndPermissions_WithTheirNulls()
    {
        var detail = await OpenedAsync();
        detail.SelectedGroup = detail.GroupChoices[0];
        detail.ChoosesLibraries = true;
        Assert.True(detail.HasNoLibraryWarning);
        detail.Libraries[1].IsChecked = true;
        detail.MetadataCuration = true;

        await detail.SaveAccessCommand.ExecuteAsync(null);

        Assert.Equal("""{"permissions":["marker_edit","metadata_curation"],"access_group_id":null,"library_ids":["6"]}""", _writes[0].Body);
    }

    [Fact]
    public async Task BackToTheGroupsLibraries_SendsNull()
    {
        _account = AdminUserPayloads.User().Replace("\"library_ids\": null", "\"library_ids\": [\"5\"]", StringComparison.Ordinal);
        var detail = await OpenedAsync();
        Assert.True(detail.ChoosesLibraries);
        Assert.True(detail.Libraries[0].IsChecked);

        detail.UsesGroupLibraries = true;
        await detail.SaveAccessCommand.ExecuteAsync(null);

        Assert.Equal("""{"library_ids":null}""", _writes[0].Body);
    }

    [Fact]
    public async Task Disable_AsksFirst_ThenSendsAtTheFreshVersion()
    {
        var detail = await OpenedAsync();
        _version = 3;

        await detail.DisableCommand.ExecuteAsync(null);

        Assert.Equal("Disable ana?", _confirmation.Asked.Single().Title);
        Assert.Equal((HttpMethod.Put, "\"v3\"", """{"enabled":false}"""), _writes.Single());
        Assert.Equal("ana is disabled", detail.Notice);
    }

    [Fact]
    public async Task Disable_ThatAnotherAdminChanged_SaysSo()
    {
        var detail = await OpenedAsync();
        _writeStatus = 412;

        await detail.DisableCommand.ExecuteAsync(null);

        Assert.True(detail.NoticeIsError);
        Assert.Equal("The account changed. Try again.", detail.Notice);
    }

    [Fact]
    public async Task AnAccountDeletedElsewhere_IsUnavailable()
    {
        _silo.Map("api/v2/admin/users/3", _ => BrowseFakeSilo.Problem(404, "not_found"));
        var detail = await OpenedAsync();

        Assert.Equal(LibraryLoadState.Unavailable, detail.State);
        Assert.Null(detail.Header);
    }
}
