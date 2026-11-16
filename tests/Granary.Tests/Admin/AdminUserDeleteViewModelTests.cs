using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Deleting an account: its name typed first, at the version read when the dialog opened.</summary>
public sealed class AdminUserDeleteViewModelTests
{
    private readonly BrowseFakeSilo _silo = AdminUserPayloads.Silo();
    private readonly FakeSessionManager _session = AdminUserPayloads.OwnerSession();
    private readonly List<(HttpMethod Method, string? IfMatch)> _writes = [];
    private int _reads;
    private int _writeStatus = 204;

    public AdminUserDeleteViewModelTests()
    {
        _silo.Map("api/v2/admin/users/3", request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return AdminUserPayloads.Versioned(AdminUserPayloads.User(), ++_reads == 1 ? "\"v1\"" : "\"v2\"");
            }
            _writes.Add((request.Method, request.Headers.TryGetValues("If-Match", out var tags) ? tags.Single() : null));
            return _writeStatus == 204 ? BrowseFakeSilo.NoContent() : BrowseFakeSilo.Problem(_writeStatus, "precondition_failed");
        });
    }

    private async Task<AdminUserDeleteViewModel> StartedAsync()
    {
        var dialog = new AdminUserDeleteViewModel(_session, new AdminUsersApi(_silo.Http), _silo.Logs.For<AdminUserDeleteViewModel>());
        Assert.True(await dialog.StartAsync("3", TestContext.Current.CancellationToken));
        return dialog;
    }

    [Fact]
    public async Task Delete_WaitsForTheName_ThenSendsTheVersion()
    {
        var dialog = await StartedAsync();
        Assert.Equal("Delete ana?", dialog.Title);

        dialog.Typed = "Ana";
        Assert.False(dialog.CanDelete);
        Assert.False(await dialog.DeleteAsync(TestContext.Current.CancellationToken));
        dialog.Typed = "ana";
        Assert.True(await dialog.DeleteAsync(TestContext.Current.CancellationToken));

        Assert.Equal([(HttpMethod.Delete, "\"v1\"")], _writes);
    }

    [Fact]
    public async Task AChangeMeanwhile_BlocksUntilReadAgain()
    {
        var dialog = await StartedAsync();
        dialog.Typed = "ana";
        _writeStatus = 412;

        Assert.False(await dialog.DeleteAsync(TestContext.Current.CancellationToken));
        Assert.True(dialog.HasConflict);
        Assert.Equal("The user changed. Reload before trying again.", dialog.Error);
        Assert.False(dialog.CanDelete);

        await dialog.ReloadAsync(TestContext.Current.CancellationToken);
        _writeStatus = 204;
        Assert.True(await dialog.DeleteAsync(TestContext.Current.CancellationToken));
        Assert.Equal("\"v2\"", _writes[^1].IfMatch);
    }

    [Fact]
    public async Task DisableInstead_DisablesAtTheVersion()
    {
        var dialog = await StartedAsync();
        dialog.DisableOffered = true;
        Assert.True(dialog.OffersDisable);

        Assert.True(await dialog.DisableAsync(TestContext.Current.CancellationToken));

        Assert.Equal([(HttpMethod.Put, "\"v1\"")], _writes);
        Assert.Equal("""{"enabled":false}""", _silo.To("api/v2/admin/users/3").Last().Body);
    }
}
