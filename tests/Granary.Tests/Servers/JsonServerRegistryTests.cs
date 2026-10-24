using System.Text.Json;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Tests.Servers;

public sealed class JsonServerRegistryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(Start);

    private string FilePath => Path.Combine(_directory, "servers.json");

    private JsonServerRegistry Create(ILogger<JsonServerRegistry>? logger = null) =>
        new(FilePath, _clock, logger ?? NullLogger<JsonServerRegistry>.Instance);

    private async Task<JsonServerRegistry> LoadedAsync()
    {
        var registry = Create();
        await registry.LoadAsync(TestContext.Current.CancellationToken);
        return registry;
    }

    [Fact]
    public async Task AddOrUpdate_PersistsAndReloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        var nas = await registry.AddOrUpdateAsync(new Uri("https://nas.example/silo"), "Living room", "server-uuid-1", ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var lan = await registry.AddOrUpdateAsync(new Uri("http://192.168.1.10:8090"), "Basement", null, ct);
        await registry.SetActiveAsync(lan.Id, ct);

        var reloaded = await LoadedAsync();

        Assert.Equal(registry.Servers, reloaded.Servers);
        Assert.Equal(lan.Id, reloaded.ActiveServerId);
        Assert.Equal(reloaded.Find(lan.Id), reloaded.Active);
        var saved = reloaded.Find(nas.Id);
        Assert.NotNull(saved);
        Assert.Equal("https://nas.example/silo", saved.BaseUri.OriginalString);
        Assert.Equal("Living room", saved.Name);
        Assert.Equal("server-uuid-1", saved.ServerId);
        Assert.Equal(Start, saved.AddedAt);
    }

    [Fact]
    public async Task File_UsesSnakeCaseAndOmitsNulls()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        var entry = await registry.AddOrUpdateAsync(new Uri("https://nas.example"), "NAS", null, ct);
        await registry.SetActiveAsync(entry.Id, ct);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath, ct));

        var root = document.RootElement;
        Assert.Equal(entry.Id, root.GetProperty("active_server_id").GetString());
        var server = Assert.Single(root.GetProperty("servers").EnumerateArray());
        Assert.Equal(entry.Id, server.GetProperty("id").GetString());
        Assert.Equal("https://nas.example", server.GetProperty("base_url").GetString());
        Assert.Equal("NAS", server.GetProperty("name").GetString());
        Assert.True(server.TryGetProperty("last_used_at", out _));
        Assert.False(server.TryGetProperty("server_id", out _));
        Assert.False(server.TryGetProperty("last_profile_id", out _));
    }

    [Fact]
    public async Task AddOrUpdate_DedupesByNormalizedUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        var first = await registry.AddOrUpdateAsync(new Uri("HTTPS://NAS.Example:443/silo/"), "Old name", "server-uuid-1", ct);

        var second = await registry.AddOrUpdateAsync(new Uri("https://nas.example/silo"), "New name", null, ct);

        var entry = Assert.Single(registry.Servers);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("New name", entry.Name);
        Assert.Equal("server-uuid-1", entry.ServerId);
        Assert.Equal("https://nas.example/silo", entry.BaseUri.OriginalString);
    }

    [Fact]
    public async Task AddOrUpdate_KeepsDifferentPathsAndPortsApart()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();

        await registry.AddOrUpdateAsync(new Uri("https://nas.example/silo"), "A", null, ct);
        await registry.AddOrUpdateAsync(new Uri("https://nas.example/Silo"), "B", null, ct);
        await registry.AddOrUpdateAsync(new Uri("https://nas.example:8443/silo"), "C", null, ct);
        await registry.AddOrUpdateAsync(new Uri("http://nas.example/silo"), "D", null, ct);

        Assert.Equal(4, registry.Servers.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public async Task Id_IsTheHexPrefixOfTheSha256OfTheNormalizedUrl()
    {
        var registry = await LoadedAsync();

        var entry = await registry.AddOrUpdateAsync(new Uri("https://NAS.example/silo/"), "NAS", null, TestContext.Current.CancellationToken);

        // printf '%s' "https://nas.example/silo" | sha256sum | cut -c1-16
        Assert.Equal("0062ea42a830212a", entry.Id);
        Assert.Equal(entry.Id, JsonServerRegistry.IdFor(new Uri("https://nas.example/silo")));
    }

    [Fact]
    public async Task AddOrUpdate_UsesTheHostName_WhenTheNameIsBlank()
    {
        var registry = await LoadedAsync();

        var entry = await registry.AddOrUpdateAsync(new Uri("https://nas.example"), "  ", null, TestContext.Current.CancellationToken);

        Assert.Equal("nas.example", entry.Name);
    }

    [Fact]
    public async Task AddOrUpdate_RejectsNonHttpAddresses()
    {
        var registry = await LoadedAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.AddOrUpdateAsync(new Uri("ftp://nas.example"), "NAS", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remove_OfTheActiveServer_ActivatesTheMostRecentlyUsedRemainingOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        var a = await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var b = await registry.AddOrUpdateAsync(new Uri("https://b.example"), "B", null, ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await registry.AddOrUpdateAsync(new Uri("https://c.example"), "C", null, ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await registry.SetActiveAsync(b.Id, ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await registry.SetActiveAsync(a.Id, ct);

        await registry.RemoveAsync(a.Id, ct);

        Assert.Null(registry.Find(a.Id));
        Assert.Equal(b.Id, registry.ActiveServerId);
        Assert.Equal(b.Id, (await LoadedAsync()).ActiveServerId);
    }

    [Fact]
    public async Task Remove_OfTheLastServer_LeavesNoneActive()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        var only = await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);
        await registry.SetActiveAsync(only.Id, ct);

        await registry.RemoveAsync(only.Id, ct);

        Assert.Empty(registry.Servers);
        Assert.Null(registry.ActiveServerId);
        Assert.Null(registry.Active);
    }

    [Fact]
    public async Task Remove_OfAnInactiveServer_KeepsTheActiveOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        var a = await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);
        var b = await registry.AddOrUpdateAsync(new Uri("https://b.example"), "B", null, ct);
        await registry.SetActiveAsync(a.Id, ct);

        await registry.RemoveAsync(b.Id, ct);

        Assert.Equal(a.Id, registry.ActiveServerId);
    }

    [Fact]
    public async Task SetActive_BumpsLastUsed_AndRejectsUnknownIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        var a = await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);
        _clock.Advance(TimeSpan.FromHours(1));

        await registry.SetActiveAsync(a.Id, ct);

        Assert.Equal(Start.AddHours(1), registry.Find(a.Id)?.LastUsedAt);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.SetActiveAsync("missing", ct));
    }

    [Fact]
    public async Task Update_PersistsChanges_ButNotANewAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        var entry = await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);

        await registry.UpdateAsync(entry with { LastProfileId = "profile-7" }, ct);

        Assert.Equal("profile-7", (await LoadedAsync()).Find(entry.Id)?.LastProfileId);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.UpdateAsync(entry with { BaseUri = new Uri("https://b.example") }, ct));
    }

    [Fact]
    public async Task Changed_IsRaisedAfterEveryChange_AndNotForNoOps()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        int changes = 0;
        registry.Changed += (_, _) => changes++;

        var entry = await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);
        await registry.UpdateAsync(entry with { Name = "Renamed" }, ct);
        await registry.SetActiveAsync(entry.Id, ct);
        await registry.RemoveAsync(entry.Id, ct);
        await registry.RemoveAsync(entry.Id, ct);

        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task Changes_BeforeLoad_KeepTheSavedServers()
    {
        var ct = TestContext.Current.CancellationToken;
        await (await LoadedAsync()).AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);

        var notLoaded = Create();
        await notLoaded.AddOrUpdateAsync(new Uri("https://b.example"), "B", null, ct);

        Assert.Equal(["A", "B"], (await LoadedAsync()).Servers.Select(s => s.Name));
    }

    [Fact]
    public async Task ConcurrentChanges_AreAllSaved()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i =>
            Task.Run(() => registry.AddOrUpdateAsync(new Uri($"https://host{i}.example"), $"Host {i}", null, ct), ct)));

        Assert.Equal(20, registry.Servers.Count);
        Assert.Equal(20, (await LoadedAsync()).Servers.Count);
    }

    [Fact]
    public async Task Load_WithoutAFile_StartsEmpty()
    {
        var registry = await LoadedAsync();

        Assert.Empty(registry.Servers);
        Assert.Null(registry.ActiveServerId);
        Assert.False(File.Exists(FilePath + ".bak"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("""{"servers": 42}""")]
    public async Task Load_OfACorruptFile_StartsEmpty_KeepsABackup_AndWarns(string content)
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, content, ct);
        var logger = new RecordingLogger();
        var registry = Create(logger);

        await registry.LoadAsync(ct);

        Assert.Empty(registry.Servers);
        Assert.Equal(content, await File.ReadAllTextAsync(FilePath + ".bak", ct));
        Assert.Contains(LogLevel.Warning, logger.Levels);
        await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);
        Assert.Single((await LoadedAsync()).Servers);
    }

    [Fact]
    public async Task Load_SkipsInvalidEntries_AndAnUnknownActiveId()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, """
            {
              "active_server_id": "gone",
              "servers": [
                { "id": "a1", "base_url": "https://a.example", "name": "A", "added_at": "2026-10-01T12:00:00Z", "last_used_at": "2026-10-01T12:00:00Z" },
                { "id": "b1", "base_url": "not a url", "name": "B", "added_at": "2026-10-01T12:00:00Z", "last_used_at": "2026-10-01T12:00:00Z" },
                { "id": "c1", "base_url": "https://A.example/", "name": "Duplicate", "added_at": "2026-10-01T12:00:00Z", "last_used_at": "2026-10-01T12:00:00Z" }
              ]
            }
            """, ct);

        string original = await File.ReadAllTextAsync(FilePath, ct);

        var registry = await LoadedAsync();

        var entry = Assert.Single(registry.Servers);
        Assert.Equal("a1", entry.Id);
        Assert.Null(registry.ActiveServerId);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath + ".bak", ct));
    }

    [Fact]
    public async Task FailedWrite_KeepsTheSavedStateAndRaisesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);
        string saved = await File.ReadAllTextAsync(FilePath, ct);
        int changes = 0;
        registry.Changed += (_, _) => changes++;
        // A directory where the temporary file must go makes the write fail.
        Directory.CreateDirectory(FilePath + ".tmp");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.AddOrUpdateAsync(new Uri("https://b.example"), "B", null, ct));

        Assert.Single(registry.Servers);
        Assert.Equal(0, changes);
        Assert.Equal(saved, await File.ReadAllTextAsync(FilePath, ct));
    }

    [Fact]
    public async Task Update_OfAnUnknownServer_DoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        int changes = 0;
        registry.Changed += (_, _) => changes++;

        await registry.UpdateAsync(new ServerEntry("missing", new Uri("https://a.example"), "A", null, Start, Start, null), ct);

        Assert.Empty(registry.Servers);
        Assert.Equal(0, changes);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task ChangedHandlers_CanCallBackIntoTheRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = await LoadedAsync();
        Task? nested = null;
        bool calledBack = false;
        registry.Changed += (_, _) =>
        {
            // Set before the call: the nested change raises Changed again, possibly synchronously.
            if (!calledBack)
            {
                calledBack = true;
                nested = registry.SetActiveAsync(registry.Servers[0].Id, ct);
            }
        };

        await registry.AddOrUpdateAsync(new Uri("https://a.example"), "A", null, ct);
        await nested!.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(registry.Servers[0].Id, registry.ActiveServerId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingLogger : ILogger<JsonServerRegistry>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
