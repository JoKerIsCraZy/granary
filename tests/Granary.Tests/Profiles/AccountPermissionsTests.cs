using System.Net;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Tests.Fakes;

namespace Granary.Tests.Profiles;

/// <summary>What the app offers beyond watching, as Silo's web decides it (lib/permissions.ts).</summary>
public sealed class AccountPermissionsTests
{
    private const string ProfilesPath = "api/v2/profiles";

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server };

    private AccountPermissions Permissions() => new(_session, new ProfilesApi(_silo.Http), new LogSink().For<AccountPermissions>());

    private static Account Account(string role, params string[] permissions) => new("1", "alex", "", role, permissions, true, false, null);

    private void Profiles() => _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.Json(HttpStatusCode.OK, """
        { "items": [ { "id": "1", "name": "Alex", "is_primary": true, "has_pin": false, "is_child": false },
                     { "id": "2", "name": "Kid", "is_primary": false, "has_pin": false, "is_child": true } ], "avatar_upload_enabled": false }
        """));

    [Fact]
    public async Task AGrantedPermission_IsEnough()
    {
        _session.Account = Account("user", AccountPermissions.MarkerEdit);
        _session.ProfileId = "2";

        Assert.True(await Permissions().HasAsync(AccountPermissions.MarkerEdit, TestContext.Current.CancellationToken));
        Assert.Empty(_silo.Sent);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("2", false)]
    [InlineData(null, true)]
    public async Task AnAdmin_ActsAsOneOnlyOnThePrimaryProfile_OrWithNone(string? profile, bool expected)
    {
        Profiles();
        _session.Account = Account("admin");
        _session.ProfileId = profile;

        Assert.Equal(expected, await Permissions().HasAsync(AccountPermissions.MarkerEdit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AViewer_HasNone()
    {
        _session.Account = Account("user");
        _session.ProfileId = "1";

        Assert.False(await Permissions().HasAsync(AccountPermissions.MarkerEdit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProfilesThatCannotBeRead_OfferNothing()
    {
        _silo.On(HttpMethod.Get, ProfilesPath, ScriptedSilo.NetworkError());
        _session.Account = Account("admin");
        _session.ProfileId = "1";

        Assert.False(await Permissions().HasAsync(AccountPermissions.MarkerEdit, TestContext.Current.CancellationToken));
    }
}
