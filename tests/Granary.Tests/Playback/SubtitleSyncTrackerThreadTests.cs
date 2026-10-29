using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;
using static Granary.Tests.Playback.SubtitleSyncTrackerTests;

namespace Granary.Tests.Playback;

/// <summary>
/// The sync state lives on the UI thread whatever thread the server's answers complete on, as in the app, where they come
/// back on the thread pool: two answers at once once corrupted its dictionaries.
/// </summary>
public sealed class SubtitleSyncTrackerThreadTests : IDisposable
{
    private const string FileId = "41";
    private const string Key = "stored-12";
    private const string ListPath = "api/v2/subtitles/" + FileId + "/sync";
    private const string KeyPath = ListPath + "/" + Key;

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SerialDispatcher _ui = new();
    private readonly SubtitleSyncTracker _tracker;
    private int _offThread;

    public SubtitleSyncTrackerThreadTests()
    {
        _tracker = new SubtitleSyncTracker(new SubtitleSyncApi(_silo.Http), _ui, _time, new LogSink().For<SubtitleSyncTracker>());
        _tracker.Changed += (_, _) => CountOffThread();
        _tracker.TimingChanged += (_, _) => CountOffThread();
        _silo.On(HttpMethod.Get, "api/v2/subtitles/sync/status", Later(Status()));
        _silo.On(HttpMethod.Get, ListPath, Later(List(State(Key))));
    }

    public void Dispose()
    {
        _ui.Read(() =>
        {
            _tracker.Dispose();
            return true;
        });
        _ui.Dispose();
    }

    private void CountOffThread()
    {
        if (!_ui.HasThreadAccess)
        {
            Interlocked.Increment(ref _offThread);
        }
    }

    // An answer that completes on the thread pool, as a real one does.
    private static Func<Task<HttpResponseMessage>> Later(string json, string? etag = null, HttpStatusCode status = HttpStatusCode.OK) => async () =>
    {
        await Task.Yield();
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }
        return response;
    };

    [Fact]
    public async Task EveryChange_ReachesItsListeners_OnTheUiThread()
    {
        await _ui.InvokeAsync(() =>
        {
            _tracker.Follow(ScriptedSilo.Base, FileId, [Key]);
            return true;
        });
        await Until(() => _ui.Read(() => _tracker.EntryOf(Key) is not null), "the subtitle was read");
        _silo.On(HttpMethod.Post, KeyPath, Later(One(State(Key, Job("j1", "running", 0.2))), status: HttpStatusCode.Accepted));
        _silo.On(HttpMethod.Get, KeyPath,
            Later(One(State(Key, Job("j1", "running", 0.6))), "\"v1\""),
            Later(One(State(Key, Job("j1", "synced", result: Timing(-900)), Timing(-900))), "\"v2\""));

        await _ui.RunAsync(() => _tracker.RequestSyncAsync(Key));
        for (int tick = 0; tick < 3; tick++)
        {
            _time.Advance(SubtitleSyncTracker.PollInterval);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        await Until(() => _ui.Read(() => _tracker.EntryOf(Key)!.Job!.Status) == "synced", "the job ended");

        Assert.Equal(0, _offThread);
        Assert.Empty(_ui.Failures);
    }

    [Fact]
    public async Task ARefusalAndAnErrorInAPollingRead_AreTakenOnTheUiThread()
    {
        await _ui.InvokeAsync(() =>
        {
            _tracker.Follow(ScriptedSilo.Base, FileId, [Key]);
            return true;
        });
        await Until(() => _ui.Read(() => _tracker.EntryOf(Key) is not null), "the subtitle was read");
        _silo.On(HttpMethod.Post, KeyPath, async () =>
        {
            await Task.Yield();
            return await ScriptedSilo.Problem(422, "unsupported")();
        });

        await _ui.RunAsync(() => _tracker.RequestSyncAsync(Key));

        Assert.Equal(0, _offThread);
        Assert.True(_ui.Read(() => _tracker.EntryOf(Key)!.IsUnsupported));
        Assert.Empty(_ui.Failures);
    }
}
