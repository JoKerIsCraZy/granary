using System.Text.Json;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;

namespace Granary.Tests.WatchParty;

/// <summary>The recent rooms as the secret store keeps them, and the identifiers the room sends both ways.</summary>
public sealed class WatchPartyRecentRoomsTests
{
    private readonly InMemorySecretStore _secrets = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();

    private WatchPartyRecentRooms Rooms(ICredentialStore? credentials = null, Granary.Core.Servers.IServerRegistry? registry = null) =>
        new(_secrets, _time, _logs.For<WatchPartyRecentRooms>(), credentials, registry);

    private WatchPartyRecentRoom Room(string id, string server = "srv1", string profile = "7", double minutesAgo = 0) =>
        new(server, profile, id, "CODE" + id, "proof-" + id, false, _time.GetUtcNow().AddMinutes(-minutesAgo));

    private string Stored => _secrets.Values[WatchPartyRecentRooms.SecretKey];

    [Fact]
    public async Task EightRooms_AreKept_PerServerAndProfile()
    {
        var rooms = Rooms();
        var ct = TestContext.Current.CancellationToken;
        for (int i = 0; i < 10; i++)
        {
            await rooms.RememberAsync(Room("a" + i, minutesAgo: 10 - i), ct);
        }
        await rooms.RememberAsync(Room("b", profile: "8", minutesAgo: 30), ct);
        await rooms.RememberAsync(Room("c", server: "srv2", minutesAgo: 30), ct);

        var mine = await rooms.ListAsync("srv1", "7", ct);
        Assert.Equal(8, mine.Count);
        Assert.Equal("a9", mine[0].RoomId);
        Assert.Single(await rooms.ListAsync("srv1", "8", ct));
        Assert.Single(await rooms.ListAsync("srv2", "7", ct));
    }

    [Fact]
    public async Task AnExpiredRoom_IsDropped_FromTheStoreToo()
    {
        var rooms = Rooms();
        var ct = TestContext.Current.CancellationToken;
        await rooms.RememberAsync(Room("old", minutesAgo: 60), ct);
        await rooms.RememberAsync(Room("new"), ct);

        _time.Advance(WatchPartyRecentRooms.MaxAge - TimeSpan.FromMinutes(30));
        Assert.Equal(["new"], (await rooms.ListAsync("srv1", "7", ct)).Select(r => r.RoomId));

        Assert.DoesNotContain("proof-old", Stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndedRoom_KeepsNoProof_AndPrintsNone()
    {
        var rooms = Rooms();
        var ct = TestContext.Current.CancellationToken;
        await rooms.RememberAsync(Room("r1"), ct);

        Assert.DoesNotContain("proof-r1", Room("r1").ToString(), StringComparison.Ordinal);
        await rooms.MarkEndedAsync("srv1", "7", "r1", ct);

        var ended = Assert.Single(await rooms.ListAsync("srv1", "7", ct));
        Assert.Equal((true, ""), (ended.Ended, ended.Token));
        Assert.DoesNotContain("proof-r1", Stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorruptStore_IsAnEmptyList_AndIsWrittenOver()
    {
        _secrets.Values[WatchPartyRecentRooms.SecretKey] = "{ not a list";
        var rooms = Rooms();
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty(await rooms.ListAsync("srv1", "7", ct));
        await rooms.RememberAsync(Room("r1"), ct);

        Assert.Equal("r1", Assert.Single(await rooms.ListAsync("srv1", "7", ct)).RoomId);
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(Stored).RootElement.ValueKind);
    }

    [Fact]
    public async Task SigningOut_TakesTheServersRoomsAlong()
    {
        var credentials = new CredentialStore(_secrets, _logs.For<CredentialStore>());
        var ct = TestContext.Current.CancellationToken;
        await credentials.SetAsync("srv1", new SessionCredentials("s", "a", "r", _time.GetUtcNow().AddHours(1), 3600, "7", null, null), ct);
        var rooms = Rooms(credentials);
        await rooms.RememberAsync(Room("r1"), ct);
        await rooms.RememberAsync(Room("r2", server: "srv2"), ct);

        await credentials.UpdateAsync("srv1", c => c with { ProfileId = "8" }, ct);
        Assert.Single(await rooms.ListAsync("srv1", "7", ct));
        await credentials.RemoveAsync("srv1", ct);

        await ServerPlaybackFixture.Until(() => !Stored.Contains("proof-r1", StringComparison.Ordinal), "the purge");
        Assert.Single(await rooms.ListAsync("srv2", "7", ct));
    }

    [Fact]
    public async Task ARemovedServer_TakesItsRoomsAlong()
    {
        var registry = new InMemoryServerRegistry();
        var ct = TestContext.Current.CancellationToken;
        var kept = await registry.AddOrUpdateAsync(new Uri("https://one.example"), "One", null, ct);
        var removed = await registry.AddOrUpdateAsync(new Uri("https://two.example"), "Two", null, ct);
        var rooms = Rooms(registry: registry);
        await rooms.RememberAsync(Room("r1", server: kept.Id), ct);
        await rooms.RememberAsync(Room("r2", server: removed.Id), ct);

        await registry.RemoveAsync(removed.Id, ct);

        await ServerPlaybackFixture.Until(() => !Stored.Contains("proof-r2", StringComparison.Ordinal), "the purge");
        Assert.Single(await rooms.ListAsync(kept.Id, "7", ct));
    }

    [Theory]
    [InlineData("\"42\"", "42")]
    [InlineData("42", "42")]
    [InlineData("null", null)]
    public void AnIdentifier_IsReadAsAStringOrANumber(string json, string? expected)
    {
        var options = new JsonSerializerOptions { Converters = { new FlexibleIdConverter() } };

        Assert.Equal(expected, JsonSerializer.Deserialize<string>(json, options));
        Assert.Equal("\"42\"", JsonSerializer.Serialize("42", options));
    }

    [Theory]
    [InlineData("4.5")]
    [InlineData("true")]
    [InlineData("{}")]
    public void AnythingElse_IsNoIdentifier(string json)
    {
        var options = new JsonSerializerOptions { Converters = { new FlexibleIdConverter() } };

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<string>(json, options));
    }
}
