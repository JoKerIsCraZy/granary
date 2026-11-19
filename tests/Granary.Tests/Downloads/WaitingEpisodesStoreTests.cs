using System.Text.Json;
using System.Text.Json.Nodes;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The episodes a season's or a series' download keeps waiting for the server's limit of downloads: in the downloads index, in
/// their order, each once, for their own profile, across starts; a sign-out takes its account's along, and one that is
/// downloaded after all waits no more.
/// </summary>
public sealed class WaitingEpisodesStoreTests : IAsyncDisposable
{
    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private static WaitingEpisode Waiting(int number, DownloadScope? scope = null) =>
        new(scope ?? Scope7, "sr_1", "Example Series", 1, $"ep_{number}", number, $"Chapter {number}", $"f_{number}", DownloadQualities.Original);

    private static string[] Ids(IEnumerable<WaitingEpisode> waiting) => [.. waiting.Select(w => w.EpisodeId)];

    private static DownloadRecord Record(string id, string episodeId) => new()
    {
        DownloadId = id,
        Scope = Scope7,
        MediaItemId = episodeId,
        SeriesId = "sr_1",
        Title = "Chapter Two",
        Quality = DownloadQualities.Original,
        Revision = 1,
        ExpectedBytes = 3000,
        State = DownloadState.Queued,
    };

    [Fact]
    public async Task WaitingEpisodes_AreKeptAcrossAStart_InTheirOrder_ForTheirOwnProfile()
    {
        await _h.Store.LoadAsync(Token);
        Assert.Equal(3, await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7), Waiting(8), Waiting(2, Scope8)], _h.Store.ClearGeneration, Token));

        await _h.RestartAsync();

        Assert.Equal(["ep_7", "ep_8"], Ids(_h.Store.WaitingIn(Scope7)));
        Assert.Equal(["ep_2"], Ids(_h.Store.WaitingIn(Scope8)));
        Assert.Equal(Waiting(7), _h.Store.WaitingIn(Scope7)[0]);
        Assert.True(_h.Store.IsWaiting(Scope7, "ep_8"));
        Assert.False(_h.Store.IsWaiting(Scope8, "ep_8"));
    }

    [Fact]
    public async Task AnEpisodeWaitingAlready_OrOnThisPc_IsNotKeptTwice()
    {
        await _h.AddAsync("dl_9", mediaItemId: "ep_9");
        await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7)], _h.Store.ClearGeneration, Token);
        int changes = 0;
        _h.Store.Changed += (_, _) => changes++;

        Assert.Equal(1, await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7), Waiting(9), Waiting(10)], _h.Store.ClearGeneration, Token));

        Assert.Equal(["ep_7", "ep_10"], Ids(_h.Store.WaitingIn(Scope7)));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task AWaitingEpisodeDownloadedAfterAll_WaitsNoMore()
    {
        await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7), Waiting(8)], _h.Store.ClearGeneration, Token);

        await _h.AddAsync("dl_7", mediaItemId: "ep_7");

        Assert.Equal(["ep_8"], Ids(_h.Store.WaitingIn(Scope7)));
        await _h.RestartAsync();
        Assert.Equal(["ep_8"], Ids(_h.Store.WaitingIn(Scope7)));
    }

    [Fact]
    public async Task RemoveWaiting_TakesOnlyTheOnesAskedFor()
    {
        await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7), Waiting(8), Waiting(7, Scope8)], _h.Store.ClearGeneration, Token);

        Assert.Equal(1, await _h.Store.RemoveWaitingAsync(Scope7, w => w.EpisodeId == "ep_7", Token));

        Assert.Equal(["ep_8"], Ids(_h.Store.WaitingIn(Scope7)));
        Assert.Equal(["ep_7"], Ids(_h.Store.WaitingIn(Scope8)));
        await _h.RestartAsync();
        Assert.Equal(["ep_8"], Ids(_h.Store.WaitingIn(Scope7)));
    }

    [Fact]
    public async Task ASignOut_TakesItsAccountsWaitingEpisodes()
    {
        var other = new DownloadScope("srv1", "2", "9");
        await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7), Waiting(8, other)], _h.Store.ClearGeneration, Token);

        await _h.Store.RemoveWhereAsync(scope => scope.AccountId == "1", _h.Store.MarkCleared("srv1", "1"), Token);

        Assert.Empty(_h.Store.WaitingIn(Scope7));
        Assert.Equal(["ep_8"], Ids(_h.Store.WaitingIn(other)));
        await _h.RestartAsync();
        Assert.Empty(_h.Store.WaitingIn(Scope7));
    }

    // Review 6: the index keeps what a waiting episode is, nothing worked out from it.
    [Fact]
    public async Task TheIndex_KeepsAWaitingEpisodesOwnMembers_AndNothingWorkedOut()
    {
        await _h.Store.LoadAsync(Token);
        await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7)], _h.Store.ClearGeneration, Token);

        using var index = JsonDocument.Parse(File.ReadAllBytes(_h.IndexPath));

        Assert.Equal(["scope", "series_id", "series_title", "season_number", "episode_id", "episode_number", "title", "media_file_id", "quality"],
            index.RootElement.GetProperty("waiting_episodes")[0].EnumerateObject().Select(p => p.Name));
    }

    // An index written before episodes could wait has no line: it loads as one without any.
    [Fact]
    public async Task AnIndexWithoutALine_LoadsAsOneWithoutWaitingEpisodes()
    {
        await _h.AddAsync("dl_1", mediaItemId: "ep_1");
        var json = JsonNode.Parse(File.ReadAllText(_h.IndexPath))!.AsObject();
        Assert.True(json.Remove("waiting_episodes"));
        File.WriteAllText(_h.IndexPath, json.ToJsonString());

        await _h.RestartAsync();

        Assert.NotNull(_h.Find("dl_1"));
        Assert.Empty(_h.Store.WaitingIn(Scope7));
    }

    // A season's run that began before a sign-out keeps nothing waiting for the account it signed out.
    [Fact]
    public async Task EpisodesFromARunBeforeASignOut_AreNotKept()
    {
        await _h.Store.LoadAsync(Token);
        long before = _h.Store.ClearGeneration;
        _h.Store.MarkCleared("srv1", "1");

        Assert.Equal(0, await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7)], before, Token));

        Assert.Empty(_h.Store.WaitingIn(Scope7));
    }
    // Issue #173: a cancel that comes after a season's run looked, but before its refused episodes are put in line, keeps none
    // of them waiting: the store looks at it in the change that would keep them.
    [Fact]
    public async Task ARunStoppedRightBeforeTheLineIsKept_KeepsNothingWaiting()
    {
        await _h.Store.LoadAsync(Token);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        Assert.Equal((0, 0), await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7)], _h.Store.ClearGeneration, stopped.Token, Token));

        Assert.Empty(_h.Store.WaitingIn(Scope7));
    }

    // Issue #173: the delete of an entry this PC never kept is queued once, however often it is asked for; one of a download
    // kept here is none, also when that download took over an entry whose delete had not been sent yet.
    [Fact]
    public async Task QueueServerDelete_QueuesEachEntryOnce_AndNoneForADownloadKeptHere()
    {
        await _h.Store.LoadAsync(Token);

        await _h.Store.QueueServerDeleteAsync(Scope7, "dl_1", Token);
        await _h.Store.QueueServerDeleteAsync(Scope7, "dl_1", Token);
        Assert.Equal(["dl_1"], _h.Store.PendingDeletes(Scope7).Select(d => d.DownloadId));

        await _h.AddAsync("dl_1", mediaItemId: "ep_1");
        Assert.Empty(_h.Store.PendingDeletes(Scope7));
        await _h.Store.QueueServerDeleteAsync(Scope7, "dl_1", Token);
        await _h.Store.QueueServerDeleteAsync(Scope8, "dl_1", Token);

        Assert.Empty(_h.Store.PendingDeletes(Scope7));
        Assert.Equal(["dl_1"], _h.Store.PendingDeletes(Scope8).Select(d => d.DownloadId));
        Assert.NotNull(_h.Find("dl_1"));
    }

    // Issue #173: a waiting episode's download that is not kept says why: its episode left the line, or its account's downloads
    // were cleared; by its id only.
    [Fact]
    public async Task ADownloadNotKept_SaysWhetherItsEpisodeLeftTheLine_OrItsAccountWasCleared()
    {
        await _h.Store.AddWaitingUnlessClearedAsync([Waiting(7), Waiting(8)], _h.Store.ClearGeneration, Token);
        long before = _h.Store.ClearGeneration;
        await _h.Store.RemoveWaitingAsync(Scope7, w => w.EpisodeId == "ep_7", Token);

        Assert.Null(await _h.Store.AddUnlessClearedAsync(Record("dl_7", "ep_7"), before, onlyWhileWaiting: true, Token));
        _h.Store.MarkCleared("srv1", "1");
        Assert.Null(await _h.Store.AddUnlessClearedAsync(Record("dl_8", "ep_8"), before, onlyWhileWaiting: true, Token));

        Assert.Contains(_h.Logs.Lines, line => line.Contains("Download dl_7 is not kept: its episode left the line meanwhile", StringComparison.Ordinal));
        Assert.Contains(_h.Logs.Lines, line => line.Contains("Download dl_8 is not kept: its account's downloads were cleared meanwhile", StringComparison.Ordinal));
    }
    // Review of #179: the store's first load tells its followers off the store's lock, so a follower that takes a lock of its
    // own first and then reads the store (the waiting line's) cannot wait on a thread that waits on it.
    [Fact]
    public async Task TheFirstLoad_TellsItsFollowers_WithoutTheStoresLockHeld()
    {
        await _h.AddAsync("dl_1", mediaItemId: "ep_1");
        var store = _h.UnreadStore();
        bool? held = null;
        store.Changed += (_, _) => held ??= store.HoldsLock;

        await store.LoadAsync(Token);

        Assert.False(held);
    }
}
