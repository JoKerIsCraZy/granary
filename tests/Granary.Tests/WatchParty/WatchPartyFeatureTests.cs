using System.Net;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.WatchParty;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>Whether the server hosts rooms, read once per server and profile, and read again when a read did not go through.</summary>
public sealed class WatchPartyFeatureTests
{
    private const string Capabilities = "api/v2/watch-together/capabilities";
    private const string Hosting = """{ "state": "available", "allowed": true, "socket_protocol": "silo.room.v2", "staged_selection": true }""";
    private const string NotHosting = """{ "state": "not_configured", "allowed": true, "socket_protocol": "" }""";

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly LogSink _logs = new();
    private readonly WatchPartyFeature _feature;

    public WatchPartyFeatureTests()
    {
        _feature = new WatchPartyFeature(_session, new WatchPartyApi(_silo.Http), new ManualTimeProvider(), _logs.For<WatchPartyFeature>());
    }

    private static Func<Task<HttpResponseMessage>> Answers(string json) => ScriptedSilo.Json(HttpStatusCode.OK, json);

    private int Reads => _silo.SentTo(HttpMethod.Get, Capabilities).Count;

    [Fact]
    public async Task AReadThatFailedUnexpectedly_IsTriedAgainByTheNextRefresh()
    {
        _silo.On(HttpMethod.Get, Capabilities,
            () => Task.FromException<HttpResponseMessage>(new InvalidOperationException("Not something a read fails with.")), Answers(Hosting));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _feature.RefreshAsync(TestContext.Current.CancellationToken));
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Reads);
        Assert.True(_feature.IsAvailable);
    }

    [Fact]
    public async Task ACancelledRead_IsTriedAgainByTheNextRefresh()
    {
        _silo.On(HttpMethod.Get, Capabilities, Answers(Hosting));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _feature.RefreshAsync(cancelled.Token));
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(_feature.IsAvailable);
    }

    [Fact]
    public async Task AReadThatWentThrough_IsNotRepeated()
    {
        _silo.On(HttpMethod.Get, Capabilities, Answers(Hosting));

        await _feature.RefreshAsync(TestContext.Current.CancellationToken);
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Reads);
    }

    [Fact]
    public async Task AnAnswerForAProfile_LeftAndPickedAgainMeanwhile_IsTaken()
    {
        var (held, gate) = ItemDetailFixture.Held();
        _silo.On(HttpMethod.Get, Capabilities, held, Answers(NotHosting));

        var first = _feature.RefreshAsync(TestContext.Current.CancellationToken);
        _session.ProfileId = "8";
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(_feature.IsAvailable);
        _session.ProfileId = "7";
        gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Hosting) });
        await first;

        Assert.True(_feature.IsAvailable);
        // Read for this profile now: the next refresh asks nothing.
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, Reads);
    }
}
