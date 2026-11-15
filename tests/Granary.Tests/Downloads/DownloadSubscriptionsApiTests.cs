using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>The server's series monitors (downloads-api.md, 8), each call sent as the account and profile it was made for.</summary>
public sealed class DownloadSubscriptionsApiTests
{
    private const string Account = "1";
    private const string Profile = "7";
    private const string ListPath = "api/v2/downloads/subscriptions";
    private const string SyncPath = "api/v2/downloads/subscriptions/sync";
    private const string EntryPath = "api/v2/downloads/subscriptions/sub_1";

    private readonly ScriptedSilo _silo = new();

    private DownloadSubscriptionsApi Api => new(_silo.Http);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Capability_ReadsWhetherSeriesCanBeMonitored()
    {
        _silo.On(HttpMethod.Get, "api/v2/capabilities/downloads",
            ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Capability(monitorQuality: false, modes: ["future", "latest_season"])),
            ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability()));

        var monitoring = await new DownloadsApi(_silo.Http).GetCapabilityAsync(ScriptedSilo.Base, Profile, Token);
        var without = await new DownloadsApi(_silo.Http).GetCapabilityAsync(ScriptedSilo.Base, Profile, Token);

        Assert.True(monitoring.CanMonitorSeries);
        Assert.Equal(["future", "latest_season"], monitoring.MonitoringModes);
        Assert.False(monitoring.MonitorQuality);
        Assert.False(without.CanMonitorSeries);
        Assert.Empty(without.MonitoringModes);
    }

    [Fact]
    public async Task List_ReadsEveryPageAsTheProfile()
    {
        _silo.On(HttpMethod.Get, ListPath,
            ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page("c2", MonitorPayloads.Monitor("sub_1", "sr_1"))),
            ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, MonitorPayloads.Monitor("sub_2", "sr_2", mode: "latest_season", targetSeason: 3))));

        var monitors = await Api.ListAsync(ScriptedSilo.Base, Account, Profile, Token);

        Assert.Equal(["sub_1", "sub_2"], monitors.Select(m => m.Id));
        Assert.Equal(3, monitors[1].TargetSeason);
        Assert.Equal("\"m1\"", monitors[0].Etag);
        Assert.All(_silo.Sent, sent => Assert.Equal((Account, Profile), (sent.AsAccount, sent.AsProfile)));
        Assert.Equal("?limit=100", _silo.Sent[0].Uri.Query);
        Assert.Equal("?limit=100&cursor=c2", _silo.Sent[1].Uri.Query);
    }

    [Fact]
    public async Task List_ThatRepeatsACursorOrAMonitor_IsNotWhole()
    {
        _silo.On(HttpMethod.Get, ListPath,
            ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page("c2", MonitorPayloads.Monitor("sub_1"))),
            ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page("c2", MonitorPayloads.Monitor("sub_2"))));

        await Assert.ThrowsAsync<HttpRequestException>(() => Api.ListAsync(ScriptedSilo.Base, Account, Profile, Token));

        _silo.On(HttpMethod.Get, ListPath,
            ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, MonitorPayloads.Monitor("sub_1"), MonitorPayloads.Monitor("sub_1"))));

        await Assert.ThrowsAsync<HttpRequestException>(() => Api.ListAsync(ScriptedSilo.Base, Account, Profile, Token));
    }

    [Fact]
    public async Task List_WhoseNextPageSaysNothingOfItsPaging_IsNotWhole()
    {
        // A first page without paging is the whole list; a page a cursor asked for must say whether more follow.
        _silo.On(HttpMethod.Get, ListPath,
            ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page("c2", MonitorPayloads.Monitor("sub_1"))),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [ """ + MonitorPayloads.Monitor("sub_2", "sr_2") + """ ] }"""));

        await Assert.ThrowsAsync<HttpRequestException>(() => Api.ListAsync(ScriptedSilo.Base, Account, Profile, Token));

        _silo.On(HttpMethod.Get, ListPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [ """ + MonitorPayloads.Monitor() + """ ] }"""));

        Assert.Single(await Api.ListAsync(ScriptedSilo.Base, Account, Profile, Token));
    }

    [Fact]
    public async Task List_WithAMonitorWithoutItsValidator_IsNotWhole()
    {
        _silo.On(HttpMethod.Get, ListPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, MonitorPayloads.Monitor(etag: ""))));

        await Assert.ThrowsAsync<HttpRequestException>(() => Api.ListAsync(ScriptedSilo.Base, Account, Profile, Token));
    }

    [Fact]
    public async Task Create_SendsTheChoicesOnce_AndLeavesSeasonsOutUnlessNamed()
    {
        _silo.On(HttpMethod.Post, ListPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(quality: "10mbps")));

        var created = await Api.CreateAsync(ScriptedSilo.Base, Account, Profile, new CreateSubscriptionBody
        {
            SeriesId = "sr_1",
            Mode = SubscriptionModes.Future,
            DeleteWatched = false,
            MaxStorageBytes = 0,
            Quality = "10mbps",
        }, Token);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(Profile, sent.AsProfile);
        Assert.Equal("""{"series_id":"sr_1","mode":"future","delete_watched":false,"max_storage_bytes":0,"quality":"10mbps"}""", sent.Body);
        Assert.Equal("sub_1", created.Id);
        Assert.Equal("10mbps", created.Quality);
    }

    [Fact]
    public async Task Create_AnsweredForAnotherSeries_IsNoAnswer()
    {
        _silo.On(HttpMethod.Post, ListPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(seriesId: "sr_9")));

        await Assert.ThrowsAsync<HttpRequestException>(() => Api.CreateAsync(ScriptedSilo.Base, Account, Profile,
            new CreateSubscriptionBody { SeriesId = "sr_1", Mode = SubscriptionModes.All }, Token));
    }

    [Fact]
    public async Task Update_SendsOnlyWhatChanged_UnderTheValidator()
    {
        _silo.On(HttpMethod.Patch, EntryPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"", deleteWatched: true)));

        var updated = await Api.UpdateAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", new UpdateSubscriptionBody { DeleteWatched = true, Active = true }, Token);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal("\"m1\"", sent.IfMatch);
        Assert.Equal(Profile, sent.AsProfile);
        Assert.Equal("""{"delete_watched":true,"active":true}""", sent.Body);
        Assert.Equal("\"m2\"", updated.Etag);
    }

    [Fact]
    public async Task Update_OfAMonitorThatChanged_SaysSo()
    {
        _silo.On(HttpMethod.Patch, EntryPath, ScriptedSilo.Problem(412, "precondition_failed"));

        var failure = await Assert.ThrowsAsync<SiloApiException>(() => Api.UpdateAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"",
            new UpdateSubscriptionBody { Active = false }, Token));

        Assert.Equal(412, failure.Status);
    }

    [Fact]
    public async Task Delete_SendsTheValidator_AndOneThatIsGoneIsDone()
    {
        _silo.On(HttpMethod.Delete, EntryPath, ScriptedSilo.NoContent(), ScriptedSilo.Problem(404, "not_found"));

        await Api.DeleteAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", Token);
        await Api.DeleteAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", Token);

        Assert.All(_silo.Sent, sent => Assert.Equal("\"m1\"", sent.IfMatch));
        Assert.Equal(2, _silo.Sent.Count);
    }

    [Fact]
    public async Task Get_ReadsOneMonitor()
    {
        _silo.On(HttpMethod.Get, EntryPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m3\"")));

        var monitor = await Api.GetAsync(ScriptedSilo.Base, Account, Profile, "sub_1", Token);

        Assert.Equal("\"m3\"", monitor.Etag);
        Assert.Equal(Profile, Assert.Single(_silo.Sent).AsProfile);
    }

    [Fact]
    public async Task SyncPage_NamesTheMonitorAndItsValidator_AndContinuesFromTheCursor()
    {
        _silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync(registered: 2, nextCursor: "n2")));

        var page = await Api.SyncPageAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", "n1", Token);

        var sent = Assert.Single(_silo.Sent);
        // The validator's quotes go as JSON escapes; the server reads the same string.
        Assert.Equal("{\"subscription_id\":\"sub_1\",\"etag\":\"\\u0022m1\\u0022\"}", sent.Body);
        Assert.Equal("?limit=100&cursor=n1", sent.Uri.Query);
        Assert.Equal((Account, Profile), (sent.AsAccount, sent.AsProfile));
        Assert.Equal(2, page.Registered);
        Assert.Equal("n2", page.Page?.NextCursor);
    }

    [Fact]
    public async Task Writes_AreBoundToTheAccountAsWellAsTheProfile()
    {
        _silo.On(HttpMethod.Post, ListPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor()))
            .On(HttpMethod.Patch, EntryPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"")))
            .On(HttpMethod.Get, EntryPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"")))
            .On(HttpMethod.Delete, EntryPath, ScriptedSilo.NoContent());

        await Api.CreateAsync(ScriptedSilo.Base, Account, Profile, new CreateSubscriptionBody { SeriesId = "sr_1", Mode = SubscriptionModes.Future }, Token);
        await Api.UpdateAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", new UpdateSubscriptionBody { Active = true }, Token);
        await Api.GetAsync(ScriptedSilo.Base, Account, Profile, "sub_1", Token);
        await Api.DeleteAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m2\"", Token);

        Assert.Equal([HttpMethod.Post, HttpMethod.Patch, HttpMethod.Get, HttpMethod.Delete], _silo.Sent.Select(sent => sent.Method));
        Assert.All(_silo.Sent, sent => Assert.Equal((Account, Profile), (sent.AsAccount, sent.AsProfile)));
        Assert.Equal([null, "\"m1\"", null, "\"m2\""], _silo.Sent.Select(sent => sent.IfMatch));
    }

    [Fact]
    public async Task ACallForAnAccountNoLongerSignedIn_IsNotSent()
    {
        // Another account signed in on the server since the call was made, and its profile has the same id.
        _silo.ActingAccount = () => "2";
        var create = new CreateSubscriptionBody { SeriesId = "sr_1", Mode = SubscriptionModes.Future };
        var update = new UpdateSubscriptionBody { Active = true };

        await Assert.ThrowsAsync<ProfileChangedException>(() => Api.CreateAsync(ScriptedSilo.Base, Account, Profile, create, Token));
        await Assert.ThrowsAsync<ProfileChangedException>(() => Api.UpdateAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", update, Token));
        await Assert.ThrowsAsync<ProfileChangedException>(() => Api.DeleteAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", Token));
        await Assert.ThrowsAsync<ProfileChangedException>(() => Api.GetAsync(ScriptedSilo.Base, Account, Profile, "sub_1", Token));
        await Assert.ThrowsAsync<ProfileChangedException>(() => Api.ListAsync(ScriptedSilo.Base, Account, Profile, Token));
        await Assert.ThrowsAsync<ProfileChangedException>(() => Api.SyncPageAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", null, Token));

        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task SyncPage_AnsweredForAnotherMonitor_IsNoAnswer()
    {
        _silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync("sub_9")));

        await Assert.ThrowsAsync<HttpRequestException>(() => Api.SyncPageAsync(ScriptedSilo.Base, Account, Profile, "sub_1", "\"m1\"", null, Token));
    }
}
