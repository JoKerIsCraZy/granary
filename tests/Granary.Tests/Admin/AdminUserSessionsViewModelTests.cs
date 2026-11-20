using Granary.Core.Api.Admin;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>An account's sign-ins on its page: listed a page at a time, signed out one by one or everywhere.</summary>
public sealed class AdminUserSessionsViewModelTests
{
    private const string Sessions = """
        { "items": [
            { "id": "6f1c2a1e-8d3b-4f0e-9a7c-2b5d8e1f3a4c", "device_name": "Living Room TV", "ip_address": "203.0.113.7",
              "created_at": "2026-10-01T12:00:00.000Z", "expires_at": "2026-11-01T12:00:00.000Z", "last_seen_at": null, "current": false }
          ], "page": { "has_more": true, "next_cursor": "n1" } }
        """;

    private static readonly AdminUser Ana = new() { Id = "3", Username = "ana", Role = "user", Enabled = true };

    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeConfirmation _confirmation = new();

    private async Task<AdminUserSessionsViewModel> OpenedAsync()
    {
        var sessions = new AdminUserSessionsViewModel(new AdminUsersApi(_silo.Http), _confirmation,
            new ManualTimeProvider(AdminUserPayloads.Now), _silo.Logs.For<AdminUserSessionsViewModel>());
        await sessions.OpenAsync(AdminActors.Of(AdminUserPayloads.OwnerSession()), Ana, manageable: true);
        return sessions;
    }

    [Fact]
    public async Task Open_ListsTheSignIns_AndLoadsMore()
    {
        _silo.Map("api/v2/admin/users/3/login-sessions", request => BrowseFakeSilo.Json(request.RequestUri!.Query.Contains("cursor=n1")
            ? """{ "items": [], "page": { "has_more": false } }""" : Sessions));
        var sessions = await OpenedAsync();

        Assert.Equal(LibraryLoadState.Ready, sessions.State);
        Assert.Equal("Living Room TV", Assert.Single(sessions.Sessions).Name);
        Assert.True(sessions.HasMore);
        await sessions.LoadMoreCommand.ExecuteAsync(null);
        Assert.False(sessions.HasMore);
        Assert.Equal("50", _silo.To("api/v2/admin/users/3/login-sessions")[0].Query("limit"));
    }

    [Fact]
    public async Task SignOut_AsksFirst_AndRemovesTheRow()
    {
        _silo.Map("api/v2/admin/users/3/login-sessions", Sessions);
        _silo.Map("api/v2/admin/users/3/login-sessions/6f1c2a1e-8d3b-4f0e-9a7c-2b5d8e1f3a4c", _ => BrowseFakeSilo.NoContent());
        var sessions = await OpenedAsync();

        await sessions.SignOutCommand.ExecuteAsync(sessions.Sessions[0]);

        Assert.Equal("Sign out Living Room TV?", _confirmation.Asked.Single().Title);
        Assert.Empty(sessions.Sessions);
        Assert.Equal("Session signed out", sessions.Notice);
    }

    [Fact]
    public async Task SignOutEverywhere_EndsThemAll()
    {
        _silo.Map("api/v2/admin/users/3/login-sessions", request => request.Method == HttpMethod.Delete
            ? BrowseFakeSilo.Json("""{ "revoked": 2 }""") : BrowseFakeSilo.Json(Sessions));
        var sessions = await OpenedAsync();

        await sessions.SignOutEverywhereCommand.ExecuteAsync(null);

        Assert.Equal("Sign ana out everywhere?", _confirmation.Asked.Single().Title);
        Assert.Empty(sessions.Sessions);
        Assert.Equal("ana is signed out everywhere", sessions.Notice);
    }
}
