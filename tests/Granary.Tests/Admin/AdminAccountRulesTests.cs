using Granary.Core.Api.Admin;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;

namespace Granary.Tests.Admin;

/// <summary>Who may do what to an account, as the web's <c>lib/accountOwner.ts</c>, and an account change's body.</summary>
public sealed class AdminAccountRulesTests
{
    private static readonly AdminUser Owner = new() { Id = "1", Role = "admin", IsOwner = true, Enabled = true };
    private static readonly AdminUser Admin = new() { Id = "2", Role = "admin", Enabled = true };
    private static readonly AdminUser User = new() { Id = "3", Role = "user", Enabled = true };

    [Fact]
    public void AnAdmin_ManagesUsersAndItself_NotOtherAdminsOrTheOwner()
    {
        Assert.True(AdminAccountRules.CanManage(User, "2", viewerIsOwner: false));
        Assert.True(AdminAccountRules.CanManage(Admin, "2", viewerIsOwner: false));
        Assert.False(AdminAccountRules.CanManage(Owner, "2", viewerIsOwner: false));
        Assert.False(AdminAccountRules.CanManage(Admin, "9", viewerIsOwner: false));
        Assert.True(AdminAccountRules.CanManage(Admin, "1", viewerIsOwner: true));
    }

    [Fact]
    public void OnlyTheOwner_ChangesAnAdminsAccess_ItsOwnToo()
    {
        Assert.False(AdminAccountRules.CanChangeAccess(Admin, "2", viewerIsOwner: false));
        Assert.True(AdminAccountRules.CanChangeAccess(User, "2", viewerIsOwner: false));
        Assert.True(AdminAccountRules.CanChangeAccess(Admin, "1", viewerIsOwner: true));
    }

    [Fact]
    public void NobodyDeletesTheOwnerOrThemselves_AndOwnershipGoesToAnEnabledAdmin()
    {
        Assert.False(AdminAccountRules.CanDelete(Owner, "1", viewerIsOwner: true));
        Assert.False(AdminAccountRules.CanDelete(Admin, "2", viewerIsOwner: false));
        Assert.True(AdminAccountRules.CanDelete(Admin, "1", viewerIsOwner: true));
        Assert.True(AdminAccountRules.CanTransferOwnership(Admin, "1", viewerIsOwner: true));
        Assert.False(AdminAccountRules.CanTransferOwnership(Admin with { Enabled = false }, "1", viewerIsOwner: true));
        Assert.False(AdminAccountRules.CanTransferOwnership(User, "1", viewerIsOwner: true));
        Assert.False(AdminAccountRules.CanTransferOwnership(Admin, "3", viewerIsOwner: false));
    }

    [Fact]
    public void AChange_SendsOnlyItsParts_AndNullsWhereTheyMeanSomething()
    {
        Assert.Equal("""{"enabled":true}""", new AdminUserChange { Enabled = true }.ToJson().ToJsonString());
        Assert.Equal("""{"access_group_id":null,"library_ids":null}""",
            new AdminUserChange { SetsGroup = true, SetsLibraries = true }.ToJson().ToJsonString());
        Assert.Equal("""{"permissions":["marker_edit"],"access_group_id":"10","library_ids":["5"]}""",
            new AdminUserChange { Permissions = ["marker_edit"], SetsGroup = true, AccessGroupId = "10", SetsLibraries = true, LibraryIds = ["5"] }
                .ToJson().ToJsonString());
        Assert.DoesNotContain("secret", new AdminUserChange { Password = "secret" }.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("elena@example.com", true)]
    [InlineData(" elena@example.com ", true)]
    [InlineData("elena@[192.168.1.10]", true)]
    [InlineData("admin@siloserver", false)]
    [InlineData("a@b@example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("elena moreau@example.com", false)]
    [InlineData("elena@example.", false)]
    public void AnEmailAddress_IsCheckedAsTheWebChecksIt(string address, bool valid)
    {
        Assert.Equal(valid, EmailAddress.IsValid(address));
    }
}
