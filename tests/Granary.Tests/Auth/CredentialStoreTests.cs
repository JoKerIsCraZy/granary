using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Tests.Fakes;

namespace Granary.Tests.Auth;

public sealed class CredentialStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemorySecretStore _secrets = new();
    private readonly LogSink _logs = new();

    private static SessionCredentials Sample(string profileId = "7") =>
        new("access-secret", "refresh-secret", Now.AddHours(8), 28800, profileId, "pin-token",
            new Account("1", "alex", "alex@example.org", "admin", ["library.manage"], true, false, null));

    private CredentialStore CreateStore() => new(_secrets, _logs.For<CredentialStore>());

    [Fact]
    public async Task Set_SurvivesANewInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateStore().SetAsync("srv1", Sample(), ct);

        var loaded = await CreateStore().GetAsync("srv1", ct);

        Assert.NotNull(loaded);
        Assert.Equal("access-secret", loaded.AccessToken);
        Assert.Equal("refresh-secret", loaded.RefreshToken);
        Assert.Equal(Now.AddHours(8), loaded.AccessTokenExpiresAt);
        Assert.Equal("7", loaded.ProfileId);
        Assert.Equal("pin-token", loaded.ProfileToken);
        Assert.True(loaded.Account!.IsAdmin);
        Assert.Equal("library.manage", Assert.Single(loaded.Account.Permissions));
        Assert.True(_secrets.Values.ContainsKey("server/srv1/session"));
    }

    [Fact]
    public async Task Update_AppliesToTheStoredValueAndReportsTheChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.SetAsync("srv1", Sample(), ct);
        var changes = new List<string>();
        store.Changed += (_, id) => changes.Add(id);

        var updated = await store.UpdateAsync("srv1", c => c with { ProfileId = "8" }, ct);
        var unchanged = await store.UpdateAsync("srv1", c => c with { ProfileId = "8" }, ct);

        Assert.Equal("8", updated!.ProfileId);
        Assert.Equal(updated, unchanged);
        Assert.Equal("srv1", Assert.Single(changes));
        Assert.Equal("8", (await CreateStore().GetAsync("srv1", ct))!.ProfileId);
    }

    [Fact]
    public async Task Update_WithoutCredentials_ReturnsNullAndWritesNothing()
    {
        var updated = await CreateStore().UpdateAsync("srv1", c => c with { ProfileId = "8" }, TestContext.Current.CancellationToken);

        Assert.Null(updated);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public async Task ConcurrentUpdates_AreNotLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.SetAsync("srv1", Sample() with { LifetimeSeconds = 0 }, ct);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => store.UpdateAsync("srv1", c => c with { LifetimeSeconds = c.LifetimeSeconds + 1 }, ct), ct)));

        Assert.Equal(50, (await store.GetAsync("srv1", ct))!.LifetimeSeconds);
    }

    [Fact]
    public async Task Remove_DeletesTheRecordAndReportsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.SetAsync("srv1", Sample(), ct);
        string? removed = null;
        store.Changed += (_, id) => removed = id;

        await store.RemoveAsync("srv1", ct);

        Assert.Null(await store.GetAsync("srv1", ct));
        Assert.Null(await CreateStore().GetAsync("srv1", ct));
        Assert.Equal("srv1", removed);
    }

    [Fact]
    public async Task UnreadableRecord_CountsAsSignedOutWithoutLoggingIt()
    {
        _secrets.Values["server/srv1/session"] = """{"access_token":"leaked-if-logged", broken""";

        var loaded = await CreateStore().GetAsync("srv1", TestContext.Current.CancellationToken);

        Assert.Null(loaded);
        Assert.Contains(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
        Assert.DoesNotContain("leaked-if-logged", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_LeavesOutTheTokens()
    {
        string text = Sample().ToString();

        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pin-token", text, StringComparison.Ordinal);
        Assert.Contains("ProfileId = 7", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(28800, 28800 - 61, false)]
    [InlineData(28800, 28800 - 60, true)]
    [InlineData(60, 29, false)]   // a 60 s token is refreshed at half its lifetime, not right away
    [InlineData(60, 30, true)]
    [InlineData(0, 0, true)]
    public void ShouldRefresh_UsesAMarginOfAMinuteCappedAtHalfTheLifetime(long lifetime, int secondsLater, bool expected)
    {
        var credentials = new SessionCredentials("a", "r", Now.AddSeconds(lifetime), lifetime, null, null, null);

        Assert.Equal(expected, credentials.ShouldRefresh(Now.AddSeconds(secondsLater)));
    }
}
