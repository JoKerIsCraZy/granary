using Granary.Core.Api.Admin;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Admin → Users: every account read page by page, then searched, filtered, sorted and shown a page at a time.</summary>
public sealed class AdminUsersViewModelTests
{
    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();

    private AdminUsersViewModel Create() =>
        new(_session, new AdminUsersApi(_silo.Http), new ManualTimeProvider(AdminUserPayloads.Now), _silo.Logs.For<AdminUsersViewModel>());

    private async Task<AdminUsersViewModel> LoadedAsync()
    {
        var users = Create();
        await users.LoadAsync();
        return users;
    }

    [Fact]
    public async Task Load_ReadsEveryPage_AndSortsByUsername()
    {
        var users = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Ready, users.State);
        Assert.Equal(["ana", "Bo", "elena", "kim"], users.Rows.Select(row => row.Username));
        Assert.Equal(2, _silo.To("api/v2/admin/users").Count);
        Assert.Equal("c1", _silo.To("api/v2/admin/users")[1].Query("cursor"));
        Assert.Equal("200", _silo.To("api/v2/admin/users")[0].Query("limit"));
        Assert.True(users.ViewerIsOwner);
        Assert.True(users.IsAvailable);
    }

    [Fact]
    public async Task Rows_NameTheRoleGroupAndStatus_AsTheWebsTable()
    {
        var users = await LoadedAsync();

        var ana = users.Rows[0];
        Assert.Equal(("user", "Family", "Active", "2d ago"), (ana.RoleText, ana.GroupText, ana.StatusText, ana.LastActiveText));
        var bo = users.Rows[1];
        Assert.Equal(("No group", "Disabled", "Never"), (bo.GroupText, bo.StatusText, bo.LastActiveText));
        var elena = users.Rows[2];
        Assert.Equal("owner", elena.RoleText);
        // The Owner is the viewer's own account: it can be edited, never deleted.
        Assert.True(elena.CanEdit);
        Assert.False(elena.CanDelete);
        Assert.True(users.Rows[3].CanDelete);
    }

    [Fact]
    public async Task Search_MatchesNameOrEmail_AndTheGroupFilterLeavesAdminsOut()
    {
        var users = await LoadedAsync();

        users.SearchText = "EXAMPLE.COM";
        Assert.Equal(4, users.Rows.Count);
        users.SearchText = "kim";
        Assert.Equal(["kim"], users.Rows.Select(row => row.Username));

        users.SearchText = "";
        users.SelectedGroup = users.GroupFilters.Single(filter => filter.Name == "Family");
        Assert.Equal(["ana"], users.Rows.Select(row => row.Username));
        users.SelectedGroup = users.GroupFilters[^1];
        Assert.Equal(["Bo"], users.Rows.Select(row => row.Username));

        users.SearchText = "nobody";
        Assert.True(users.HasNoMatches);
    }

    [Fact]
    public async Task SortBy_TurnsTheOrderRound_AndDatesStartNewestFirst()
    {
        var users = await LoadedAsync();

        users.SortByCommand.Execute(AdminUserSort.Username);
        Assert.Equal(["kim", "elena", "Bo", "ana"], users.Rows.Select(row => row.Username));

        users.SortByCommand.Execute(AdminUserSort.LastActive);
        Assert.True(users.SortDescending);
        // Never seen goes last either way.
        Assert.Equal(["elena", "ana", "Bo", "kim"], users.Rows.Select(row => row.Username));

        users.SortByCommand.Execute(AdminUserSort.Created);
        Assert.Equal(["Bo", "ana", "kim", "elena"], users.Rows.Select(row => row.Username));
    }

    [Fact]
    public async Task Pages_ShowTheRange_AndStepThrough()
    {
        var many = string.Join(",", Enumerable.Range(10, 30).Select(i => AdminUserPayloads.User(i.ToString(), $"user{i}")));
        _silo.Map("api/v2/admin/users", $$"""{ "items": [{{many}}], "page": { "has_more": false } }""");
        var users = await LoadedAsync();

        Assert.True(users.HasPages);
        Assert.Equal("Showing 1-25 of 30", users.PageRangeText);
        Assert.False(users.PreviousPageCommand.CanExecute(null));
        users.NextPageCommand.Execute(null);
        Assert.Equal("Showing 26-30 of 30", users.PageRangeText);
        Assert.Equal(5, users.Rows.Count);
        Assert.False(users.NextPageCommand.CanExecute(null));

        users.PageSize = 50;
        Assert.False(users.HasPages);
        Assert.Equal(30, users.Rows.Count);
    }

    [Fact]
    public async Task AFailedRead_SaysSo_AndAFailedGroupReadLeavesTheList()
    {
        _silo.Map("api/v2/admin/access-groups", _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var users = await LoadedAsync();
        Assert.True(users.GroupsFailed);
        Assert.Equal("Unknown group", users.Rows[0].GroupText);

        _silo.Map("api/v2/admin/users", _ => BrowseFakeSilo.Problem(500, "internal_error"));
        await users.LoadAsync();
        Assert.True(users.LoadFailed);
        // What was read before stays up.
        Assert.Equal(LibraryLoadState.Ready, users.State);

        var fresh = await LoadedAsync();
        Assert.Equal(LibraryLoadState.Error, fresh.State);
        Assert.DoesNotContain("elena@example.com", _silo.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedCursor_FailsTheRead()
    {
        _silo.Map("api/v2/admin/users", AdminUserPayloads.Users);
        var users = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Error, users.State);
        Assert.Equal(2, _silo.To("api/v2/admin/users").Count);
    }

    [Fact]
    public async Task AServerWithoutAdministration_IsUnavailable()
    {
        _silo.Map("api/v2/admin/users/capabilities", """{ "state": "not_configured", "available": false }""");
        var users = await LoadedAsync();

        Assert.False(users.IsAvailable);
    }

    [Fact]
    public async Task OpenUser_AsksForTheAccount()
    {
        var users = await LoadedAsync();
        string? opened = null;
        users.UserRequested += (_, id) => opened = id;

        users.OpenUserCommand.Execute(users.Rows[0]);

        Assert.Equal("3", opened);
    }
}
