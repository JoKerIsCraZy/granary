using System.Net;
using System.Text;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>
/// Whether a library has box sets on, as the web's <c>useItemBoxSet</c> checks before a movie asks for its box set: one
/// read shared by every page that asks, kept a few minutes for the profile it was read for, and never a failure.
/// </summary>
public sealed class BoxSetLibrariesTests
{
    private const string LibrariesPath = "api/v2/user/libraries";

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };

    private static Uri Server => ScriptedSilo.Server.BaseUri;

    private static CancellationToken Test => TestContext.Current.CancellationToken;

    private BoxSetLibraries Create() => new(new CatalogApi(_silo.Http), _session, _time, _logs.For<BoxSetLibraries>());

    private static Func<Task<HttpResponseMessage>> Libraries(bool boxSets) => ScriptedSilo.Json(HttpStatusCode.OK, BoxSetPayloads.Libraries(boxSets));

    private static HttpResponseMessage LibrariesResponse(bool boxSets) =>
        new(HttpStatusCode.OK) { Content = new StringContent(BoxSetPayloads.Libraries(boxSets), Encoding.UTF8, "application/json") };

    private int Reads => _silo.SentTo(HttpMethod.Get, LibrariesPath).Count;

    // An answer the test releases when it wants.
    private TaskCompletionSource<HttpResponseMessage> Held()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Get, LibrariesPath, () => answer.Task);
        return answer;
    }

    [Fact]
    public async Task TheAnswer_IsKeptForFiveMinutes_AsTheWebsStaleTime()
    {
        _silo.On(HttpMethod.Get, LibrariesPath, Libraries(boxSets: false), Libraries(boxSets: true));
        var libraries = Create();

        Assert.False(await libraries.AnyAsync(Server, "7", Test));
        _time.Advance(BoxSetLibraries.KeepFor - TimeSpan.FromSeconds(1));
        Assert.False(await libraries.AnyAsync(Server, "7", Test));
        Assert.Equal(1, Reads);

        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await libraries.AnyAsync(Server, "7", Test));
        Assert.Equal(2, Reads);
    }

    [Fact]
    public async Task TwoPagesAskingAtOnce_ShareOneRead()
    {
        var answer = Held();
        var libraries = Create();

        var first = libraries.AnyAsync(Server, "7", Test);
        var second = libraries.AnyAsync(Server, "7", Test);
        answer.SetResult(LibrariesResponse(boxSets: true));

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, Reads);
    }

    [Fact]
    public async Task APageThatLeaves_AnswersNo_AndTheOthersStillGetTheRead()
    {
        var answer = Held();
        var libraries = Create();
        using var leaving = new CancellationTokenSource();

        var left = libraries.AnyAsync(Server, "7", leaving.Token);
        var staying = libraries.AnyAsync(Server, "7", Test);
        await leaving.CancelAsync();

        Assert.False(await left);
        answer.SetResult(LibrariesResponse(boxSets: true));
        Assert.True(await staying);
        Assert.True(await libraries.AnyAsync(Server, "7", Test));
        Assert.Equal(1, Reads);
    }

    [Fact]
    public async Task AnyFailure_AnswersNo_AndIsNotKept()
    {
        // Not only an HTTP failure: the sign-in's own steps can throw anything on the way out.
        _silo.On(HttpMethod.Get, LibrariesPath, () => Task.FromException<HttpResponseMessage>(new InvalidOperationException("refresher failed")),
            Libraries(boxSets: true));
        var libraries = Create();

        Assert.False(await libraries.AnyAsync(Server, "7", Test));
        Assert.True(await libraries.AnyAsync(Server, "7", Test));
        Assert.Equal(2, Reads);
        Assert.Contains("InvalidOperationException", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatCameForAnotherProfile_IsNotKept()
    {
        // The read goes out as whichever profile acts when it is sent: after a switch it may be the new one's.
        var answer = Held();
        var libraries = Create();
        var read = libraries.AnyAsync(Server, "7", Test);

        _session.ProfileId = "8";
        answer.SetResult(LibrariesResponse(boxSets: true));
        await read;
        _session.ProfileId = "7";
        _silo.On(HttpMethod.Get, LibrariesPath, Libraries(boxSets: false));

        Assert.False(await libraries.AnyAsync(Server, "7", Test));
        Assert.Equal(2, Reads);
    }

    [Fact]
    public async Task ASessionChange_ForgetsEverything_AndKeepsNoReadOnItsWay()
    {
        _silo.On(HttpMethod.Get, LibrariesPath, Libraries(boxSets: true));
        var libraries = Create();
        Assert.True(await libraries.AnyAsync(Server, "7", Test));
        var answer = Held();

        // Signed out and in again: what was kept goes, and so does what is on its way.
        _session.RaiseChanged();
        var read = libraries.AnyAsync(Server, "7", Test);
        _session.RaiseChanged();
        answer.SetResult(LibrariesResponse(boxSets: true));
        await read;
        _silo.On(HttpMethod.Get, LibrariesPath, Libraries(boxSets: false));

        Assert.False(await libraries.AnyAsync(Server, "7", Test));
        Assert.Equal(3, Reads);
    }

    [Fact]
    public async Task ARead_IsGivenUpAfter30Seconds_AndTriedAgainNextTime()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.OnCancellable(HttpMethod.Get, LibrariesPath, async cancellationToken =>
        {
            sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return LibrariesResponse(boxSets: true);
        });
        var libraries = Create();
        var read = libraries.AnyAsync(Server, "7", Test);
        await sent.Task.WaitAsync(Test);

        _time.Advance(BoxSetLibraries.GiveUpAfter);

        Assert.False(await read);
        _silo.OnCancellable(HttpMethod.Get, LibrariesPath, _ => Task.FromResult(LibrariesResponse(boxSets: true)));
        Assert.True(await libraries.AnyAsync(Server, "7", Test));
        Assert.Equal(2, Reads);
    }
}
