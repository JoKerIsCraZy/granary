using System.Text.Json;
using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Admin → Users → Add User: the account, its role and group, its permissions and first profile.</summary>
public sealed class AdminNewUserViewModelTests
{
    private static readonly AdminAccessGroup[] Groups = [new() { Id = "10", Name = "Family", IsDefault = true }, new() { Id = "11", Name = "Friends" }];

    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();

    private AdminNewUserViewModel Started(bool groupsKnown = true, bool owner = true, bool defaultProfile = true)
    {
        var form = new AdminNewUserViewModel(new AdminUsersApi(_silo.Http), _silo.Logs.For<AdminNewUserViewModel>());
        form.Start(AdminActors.Of(_session), new AdminUserCapabilities { Available = true, DefaultProfile = defaultProfile }, groupsKnown ? Groups : [], groupsKnown, owner);
        form.Username = "ana";
        form.Email = "ana@example.com";
        form.Password = "margin fossil";
        return form;
    }

    private JsonElement Sent() => JsonDocument.Parse(_silo.To("api/v2/admin/users").Single(r => r.Method == HttpMethod.Post).Body!).RootElement;

    [Fact]
    public async Task Save_SendsTheAccount_InTheDefaultGroup_WithMarkerEditing()
    {
        _silo.Map("api/v2/admin/users", _ => BrowseFakeSilo.Json("""{ "id": "9" }""", System.Net.HttpStatusCode.Created));
        var form = Started();

        Assert.Equal("Family", form.SelectedGroupChoice?.Name);
        Assert.True(await form.SaveAsync(TestContext.Current.CancellationToken));

        var body = Sent();
        Assert.Equal(("ana", "ana@example.com", "margin fossil", "user"),
            (body.GetProperty("username").GetString(), body.GetProperty("email").GetString(), body.GetProperty("password").GetString(),
                body.GetProperty("role").GetString()));
        Assert.Equal("10", body.GetProperty("access_group_id").GetString());
        Assert.Equal(["marker_edit"], body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.True(body.GetProperty("create_default_profile").GetBoolean());
        Assert.False(body.TryGetProperty("require_password_change", out _));
        Assert.Equal("", form.Password);
    }

    [Fact]
    public async Task AnAdmin_JoinsNoGroup_AndATemporaryPasswordIsSaid()
    {
        _silo.Map("api/v2/admin/users", _ => BrowseFakeSilo.Json("""{ "id": "9" }""", System.Net.HttpStatusCode.Created));
        var form = Started();
        form.IsAdminRole = true;
        form.RequirePasswordChange = true;
        form.MetadataCuration = true;

        Assert.False(form.CanPickGroup);
        await form.SaveAsync(TestContext.Current.CancellationToken);

        var body = Sent();
        Assert.Equal("admin", body.GetProperty("role").GetString());
        Assert.False(body.TryGetProperty("access_group_id", out _));
        Assert.True(body.GetProperty("require_password_change").GetBoolean());
        Assert.Equal(["marker_edit", "metadata_curation"], body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task GroupsNotRead_LetTheServerChoose()
    {
        _silo.Map("api/v2/admin/users", _ => BrowseFakeSilo.Json("""{ "id": "9" }""", System.Net.HttpStatusCode.Created));
        var form = Started(groupsKnown: false, owner: false);

        Assert.Equal("Default group", form.SelectedGroupChoice?.Name);
        Assert.True(form.IsAdminLocked);
        await form.SaveAsync(TestContext.Current.CancellationToken);

        Assert.False(Sent().TryGetProperty("access_group_id", out _));
    }

    [Fact]
    public async Task AMalformedEmail_IsNotSent()
    {
        var form = Started();
        form.Email = "admin@siloserver";

        Assert.False(await form.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Enter a valid email address, like name@example.com", form.Error);
        Assert.Empty(_silo.To("api/v2/admin/users"));
    }

    [Fact]
    public async Task ATakenName_IsSaid_AndThePasswordIsNotLogged()
    {
        _silo.Map("api/v2/admin/users", _ => BrowseFakeSilo.Problem(409, "conflict"));
        var form = Started();

        Assert.False(await form.SaveAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Username or email already in use", form.Error);
        Assert.DoesNotContain("margin fossil", _silo.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutDefaultProfiles_OnlyAnAccountWithoutOneIsMade()
    {
        _silo.Map("api/v2/admin/users", _ => BrowseFakeSilo.Json("""{ "id": "9" }""", System.Net.HttpStatusCode.Created));
        var form = Started(defaultProfile: false);

        Assert.False(await form.SaveAsync(TestContext.Current.CancellationToken));
        form.CreateDefaultProfile = false;
        Assert.True(await form.SaveAsync(TestContext.Current.CancellationToken));
        Assert.False(Sent().GetProperty("create_default_profile").GetBoolean());
    }
}
