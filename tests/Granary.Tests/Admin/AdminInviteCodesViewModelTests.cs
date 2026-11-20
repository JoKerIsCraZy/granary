using System.Net;
using System.Text.Json;
using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Admin → Users → Invite Codes, as the web's InviteCodesTab: the list, Create Code, a row's switch, Add Uses and Delete.</summary>
public sealed class AdminInviteCodesViewModelTests
{
    private const string Page1 = """
        { "items": [ { "id": "2", "code": "SPRING2026AB", "label": "Book club", "max_uses": 10, "use_count": 10, "created_by": "1",
                       "enabled": true, "created_at": "2026-10-01T10:00:00.000Z", "updated_at": "2026-10-01T10:00:00.000Z" } ],
          "page": { "has_more": true, "next_cursor": "p2" } }
        """;

    private const string Page2 = """
        { "items": [ { "id": "1", "code": "WINTERCODE00", "label": "", "max_uses": 5, "use_count": 1, "created_by": "1",
                       "enabled": false, "created_at": "2026-09-01T10:00:00.000Z", "updated_at": "2026-09-01T10:00:00.000Z" } ],
          "page": { "has_more": false } }
        """;

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();
    private readonly FakeConfirmation _confirmation = new();

    public AdminInviteCodesViewModelTests()
    {
        _silo.Map("api/v2/admin/invite-codes", request => request.Method == HttpMethod.Get
            ? BrowseFakeSilo.Json(request.RequestUri!.Query.Contains("cursor=p2") ? Page2 : Page1)
            : BrowseFakeSilo.Json("""{ "id": "3", "code": "X", "label": "", "max_uses": 1, "use_count": 0, "enabled": true }""", HttpStatusCode.Created));
        _silo.Map("api/v2/admin/settings/effective", """{ "signup.enabled": "false", "server.public_url": "https://example.com" }""");
    }

    private AdminInviteCodesViewModel Tab() => new(_session, new AdminInviteCodesApi(_silo.Http), new AdminUsersApi(_silo.Http), _confirmation,
        _silo.Logs.For<AdminInviteCodesViewModel>());

    private JsonElement LastBody(string path, HttpMethod method) =>
        JsonDocument.Parse(_silo.To(path).Last(request => request.Method == method).Body!).RootElement;

    [Fact]
    public async Task Open_ReadsTheCodes_AndWhetherSignupsAreOn()
    {
        var tab = Tab();

        await tab.OpenAsync();

        var row = Assert.Single(tab.Rows);
        Assert.Equal(("SPRING2026AB", "Book club", "10 / 10", true, "Active"), (row.Code, row.LabelText, row.UsageText, row.IsUsedUp, row.StatusText));
        Assert.True(tab.SignupsOffShown);
        Assert.Equal("50", _silo.To("api/v2/admin/invite-codes").First().Query("limit"));

        await tab.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(2, tab.Rows.Count);
        Assert.Equal("-", tab.Rows[1].LabelText);
        Assert.False(tab.HasMore);
    }

    [Fact]
    public async Task Create_SendsTheWebsBody_WithAFreshCode()
    {
        var tab = Tab();
        await tab.OpenAsync();
        tab.StartCreate();

        Assert.Matches("^[0-9A-F]{16}$", tab.NewCode);
        tab.NewCode = "book club 2026";
        Assert.True(await tab.CreateAsync());

        var body = LastBody("api/v2/admin/invite-codes", HttpMethod.Post);
        Assert.Equal("BOOK CLUB 2026", body.GetProperty("code").GetString());
        Assert.Equal("", body.GetProperty("label").GetString());
        Assert.Equal(10, body.GetProperty("max_uses").GetInt32());
        Assert.Equal("Invite code created", tab.Message);
    }

    [Fact]
    public async Task AMaxUsesNotPositive_IsNotSent()
    {
        var tab = Tab();
        await tab.OpenAsync();
        tab.StartCreate();
        tab.NewMaxUses = "0";

        Assert.False(await tab.CreateAsync());

        Assert.Equal("Max uses must be a positive number", tab.FormError);
        Assert.DoesNotContain(_silo.To("api/v2/admin/invite-codes"), request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task TheSwitch_SendsTheOtherState()
    {
        _silo.Map("api/v2/admin/invite-codes/2", _ => BrowseFakeSilo.NoContent());
        var tab = Tab();
        await tab.OpenAsync();

        await tab.ToggleAsync(tab.Rows[0]);

        Assert.False(LastBody("api/v2/admin/invite-codes/2", HttpMethod.Put).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task AddUses_SendsTheAmount_AndAFailureReadsTheListAgain()
    {
        _silo.Map("api/v2/admin/invite-codes/2/top-up", _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var tab = Tab();
        await tab.OpenAsync();
        int reads = _silo.To("api/v2/admin/invite-codes").Count;
        tab.StartTopUp(tab.Rows[0]);
        tab.AdditionalUses = "5";

        Assert.False(await tab.TopUpAsync());

        Assert.Equal(5, LastBody("api/v2/admin/invite-codes/2/top-up", HttpMethod.Post).GetProperty("additional_uses").GetInt32());
        Assert.StartsWith("Check the code’s current maximum uses", tab.FormError, StringComparison.Ordinal);
        Assert.True(_silo.To("api/v2/admin/invite-codes").Count > reads);
    }

    [Fact]
    public async Task Delete_IsAskedFirst()
    {
        _silo.Map("api/v2/admin/invite-codes/2", _ => BrowseFakeSilo.NoContent());
        var tab = Tab();
        await tab.OpenAsync();
        _confirmation.Answer = false;

        await tab.DeleteAsync(tab.Rows[0]);
        Assert.Empty(_silo.To("api/v2/admin/invite-codes/2"));
        Assert.Contains("SPRING2026AB", _confirmation.Asked.Single().Message, StringComparison.Ordinal);

        _confirmation.Answer = true;
        await tab.DeleteAsync(tab.Rows[0]);
        Assert.Single(_silo.To("api/v2/admin/invite-codes/2"), request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task TheCodes_AreNeverLogged()
    {
        _silo.Map("api/v2/admin/invite-codes/2", _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var tab = Tab();
        await tab.OpenAsync();

        await tab.ToggleAsync(tab.Rows[0]);
        await tab.DeleteAsync(tab.Rows[0]);

        Assert.DoesNotContain("SPRING2026AB", _silo.Logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("SPRING2026AB", tab.Rows[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SPRING2026AB", tab.Rows[0].Source.ToString(), StringComparison.Ordinal);
    }
}
