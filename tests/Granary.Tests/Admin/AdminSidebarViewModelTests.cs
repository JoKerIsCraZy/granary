using Granary.Core.Api.Admin;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Admin;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>The admin sidebar's build and live count, as the web's AdminSidebar.</summary>
public sealed class AdminSidebarViewModelTests
{
    private readonly BrowseFakeSilo _silo = AdminPayloads.Dashboard();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly TestClock _clock = new(AdminPayloads.Now);

    private AdminSidebarViewModel Create() => new(_session, new AdminApi(_silo.Http), _clock, _silo.Logs.For<AdminSidebarViewModel>());

    [Fact]
    public async Task Refresh_ReadsTheBuildOnce_AndTheLiveCountWhenStale()
    {
        var sidebar = Create();
        Assert.Equal("loading...", sidebar.BuildText);

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1715 · 13ad7a74", sidebar.BuildText);
        Assert.True(sidebar.HasLive);
        Assert.Equal("2 live", sidebar.LiveText);

        _clock.Now = AdminPayloads.Now.AddSeconds(20);
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        _clock.Now = AdminPayloads.Now.AddSeconds(40);
        _silo.Map("api/v2/admin/sessions", AdminPayloads.NoSessions);
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Single(_silo.To("api/v2/admin/system/build"));
        Assert.Equal(2, _silo.To("api/v2/admin/sessions").Count);
        Assert.False(sidebar.HasLive);
    }

    [Theory]
    [InlineData("""{ "available": false, "build_number": 0, "display": "", "revision": "", "dirty": false }""", "dev build")]
    [InlineData("""{ "available": true, "build_number": 0, "display": "v1.2.0", "revision": "abc", "dirty": false }""", "v1.2.0")]
    public async Task Build_ReadsAsTheWebWordsIt(string build, string expected)
    {
        _silo.Map("api/v2/admin/system/build", build);
        var sidebar = Create();

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, sidebar.BuildText);
    }

    [Fact]
    public async Task FailedReads_SayLoadFailed_AndKeepTheCount()
    {
        var sidebar = Create();
        sidebar.Show(null, 3);
        _clock.Now = AdminPayloads.Now.AddMinutes(1);
        _silo.Map("api/v2/admin/system/build", _ => BrowseFakeSilo.Problem(503, "dependency_unavailable"));
        _silo.Map("api/v2/admin/sessions", _ => throw new HttpRequestException("down"));

        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("load failed", sidebar.BuildText);
        Assert.Equal(3, sidebar.LiveCount);
    }

    [Fact]
    public async Task AnotherProfile_StartsOver()
    {
        var sidebar = Create();
        sidebar.Show(new AdminBuildInfo(true, "13ad7a74", 1715, "13ad7a74f", false), 4);

        _session.ProfileId = "8";
        sidebar.Show(null, null);

        Assert.Equal("loading...", sidebar.BuildText);
        Assert.Equal(0, sidebar.LiveCount);
        _session.State = SessionState.SignedOut;
        await sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_silo.Requests);
    }
}
