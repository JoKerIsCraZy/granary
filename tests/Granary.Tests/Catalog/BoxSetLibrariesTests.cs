using System.Net;
using Granary.Core.Api.Catalog;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Catalog;

/// <summary>Whether a library has box sets on, as the web's <c>useItemBoxSet</c> checks before a movie asks for its box set.</summary>
public sealed class BoxSetLibrariesTests
{
    private const string LibrariesPath = "api/v2/user/libraries";

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();

    private BoxSetLibraries Create() => new(new CatalogApi(_silo.Http), _time, _logs.For<BoxSetLibraries>());

    private static Func<Task<HttpResponseMessage>> Libraries(bool boxSets) => ScriptedSilo.Json(HttpStatusCode.OK, BoxSetPayloads.Libraries(boxSets));

    private int Reads => _silo.SentTo(HttpMethod.Get, LibrariesPath).Count;

    [Fact]
    public async Task TheList_IsKeptForFiveMinutes_AsTheWebsStaleTime()
    {
        _silo.On(HttpMethod.Get, LibrariesPath, Libraries(boxSets: false), Libraries(boxSets: true));
        var libraries = Create();
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await libraries.AnyAsync(ScriptedSilo.Server, "7", ct));
        _time.Advance(BoxSetLibraries.StaleAfter - TimeSpan.FromSeconds(1));
        Assert.False(await libraries.AnyAsync(ScriptedSilo.Server, "7", ct));
        Assert.Equal(1, Reads);

        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await libraries.AnyAsync(ScriptedSilo.Server, "7", ct));
        Assert.Equal(2, Reads);
    }

    [Fact]
    public async Task AnotherProfile_ReadsItsOwn()
    {
        _silo.On(HttpMethod.Get, LibrariesPath, Libraries(boxSets: true));
        var libraries = Create();
        var ct = TestContext.Current.CancellationToken;

        await libraries.AnyAsync(ScriptedSilo.Server, "7", ct);
        await libraries.AnyAsync(ScriptedSilo.Server, "8", ct);

        Assert.Equal(2, Reads);
    }

    [Fact]
    public async Task AFailedRead_AnswersNo_AndIsTriedAgainNextTime()
    {
        _silo.On(HttpMethod.Get, LibrariesPath, ScriptedSilo.Problem(503, "dependency_unavailable"), Libraries(boxSets: true));
        var libraries = Create();
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await libraries.AnyAsync(ScriptedSilo.Server, "7", ct));
        Assert.True(await libraries.AnyAsync(ScriptedSilo.Server, "7", ct));
        Assert.Equal(2, Reads);
    }
}
