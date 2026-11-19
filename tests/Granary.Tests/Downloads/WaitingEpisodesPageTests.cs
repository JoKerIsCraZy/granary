using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Episodes waiting for the server's limit of downloads where the viewer sees them: the Downloads page says how many wait,
/// with Cancel, which takes them out of line; deleting a series' downloads takes its waiting episodes along; a waiting episode's
/// own Download says it is queued and opens the Downloads page.
/// </summary>
public sealed class WaitingEpisodesPageTests : IAsyncDisposable
{
    private const string CreatePath = "api/v2/downloads";

    private readonly DownloadHarness _h = new();
    private readonly FakeConfirmation _confirmation = new();
    private DownloadManager? _manager;

    // A manager asks for the waiting episodes once as it starts: the server's limit still holds, unless a test says otherwise.
    public WaitingEpisodesPageTests()
    {
        _h.List();
        _h.Silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Problem(429, ProblemCodes.RateLimited));
    }

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private static WaitingEpisode Waiting(string episodeId, string seriesId = "sr_1") =>
        new(Scope7, seriesId, "Example Series", 1, episodeId, 2, "Chapter Two", null, DownloadQualities.Original);

    private async Task WaitAsync(params WaitingEpisode[] episodes) =>
        await _h.Store.AddWaitingUnlessClearedAsync(episodes, _h.Store.ClearGeneration, Token);

    private async Task<DownloadsViewModel> PageAsync()
    {
        _manager = _h.NewManager();
        await _manager.WaitingRun;
        var page = new DownloadsViewModel(_manager, _h.Disk, _h.Preferences, new RecordingNavigation(), _confirmation, new ImmediateDispatcher(),
            _h.Time, _h.Logs.For<DownloadsViewModel>());
        page.Attach();
        await page.SpaceRead;
        return page;
    }

    [Fact]
    public async Task EpisodesWaitingForTheLimit_ShowANoteOnTheDownloadsPage()
    {
        await WaitAsync(Waiting("ep_2"), Waiting("ep_3"));

        var page = await PageAsync();

        Assert.True(page.ShowsWaitingNote);
        Assert.Equal("Your Silo server's download limit is reached. 2 episodes wait on this PC and start once the server allows more.", page.WaitingNote);
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task WithoutWaitingEpisodes_ThereIsNoNote()
    {
        var page = await PageAsync();

        Assert.False(page.ShowsWaitingNote);
        Assert.True(page.IsEmpty);
    }

    [Fact]
    public async Task CancelOnTheNote_TakesThemOutOfLine()
    {
        await WaitAsync(Waiting("ep_2"), Waiting("ep_3", "sr_2"));
        var page = await PageAsync();

        await page.CancelWaitingCommand.ExecuteAsync(null);

        Assert.Empty(_h.Store.WaitingIn(Scope7));
        Assert.False(page.ShowsWaitingNote);
    }

    [Fact]
    public async Task DeletingASeriesDownloads_TakesItsWaitingEpisodesAlong()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1", change: r => r with { SeriesId = "sr_1", SeriesTitle = "Example Series" });
        await WaitAsync(Waiting("ep_2"), Waiting("ep_9", "sr_2"));
        var page = await PageAsync();

        await page.Actions.DeleteSeriesCommand.ExecuteAsync(page.Series.Single().Card);

        Assert.Equal(["ep_9"], _h.Store.WaitingIn(Scope7).Select(w => w.EpisodeId));
        Assert.Null(_h.Find("dl_1"));
    }

    // The line goes before the downloads: a delete that reaches the server frees a place, which must not start the series'
    // waiting episode on this PC again.
    [Fact]
    public async Task DeletingASeriesDownloads_StartsNoneOfItsWaitingEpisodes_ThoughTheServerFreesAPlace()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1", change: r => r with { SeriesId = "sr_1", SeriesTitle = "Example Series" });
        await WaitAsync(Waiting("ep_2"));
        var page = await PageAsync();
        _h.Silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_2", "ready", episodeId: "ep_2"))));

        await page.Actions.DeleteSeriesCommand.ExecuteAsync(page.Series.Single().Card);
        await _h.Store.ForgetPendingDeleteAsync(_h.Store.PendingDeletes(Scope7).Single(), Token);
        await _manager!.WaitingRun;

        Assert.Empty(_h.Store.WaitingIn(Scope7));
        Assert.Null(_h.Find("dl_2"));
    }

    // Review 7 and the security review: the series' delete does not wait for an ask under way, which may hang on the network;
    // that ask's download, answered after the episode left the line, is not kept, and the server hears its delete.
    [Fact]
    public async Task DeletingASeries_WhileItsWaitingEpisodeIsAsked_DoesNotWait_AndKeepsNothingOfIt()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1", change: r => r with { SeriesId = "sr_1", SeriesTitle = "Example Series" });
        await WaitAsync(Waiting("ep_2"));
        var page = await PageAsync();
        var (held, gate) = ItemDetailFixture.Held();
        _h.Silo.On(HttpMethod.Post, CreatePath, held);
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await Until(() => _h.Silo.SentTo(HttpMethod.Post, CreatePath).Count == 2, "the waiting episode was asked again");

        await page.Actions.DeleteSeriesCommand.ExecuteAsync(page.Series.Single().Card).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Empty(_h.Store.WaitingIn(Scope7));
        gate.SetResult(await ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_2", "ready", episodeId: "ep_2")))());
        await _manager!.WaitingRun;

        Assert.Empty(_h.Store.In(Scope7));
        Assert.Contains(_h.Store.PendingDeletes(Scope7), d => d.DownloadId == "dl_2");
    }

    // Issue #173: a download of the series kept while the question showed goes with the others, also when another profile
    // became active between the line's drop and the read of the series' downloads.
    [Fact]
    public async Task DeletingASeries_TakesADownloadKeptBeforeTheDrop_ThoughTheProfileSwitchedAfterIt()
    {
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "ep_1", change: r => r with { SeriesId = "sr_1", SeriesTitle = "Example Series" });
        await _h.AddAsync("dl_9", DownloadState.Completed, mediaItemId: "ep_9", change: r => r with { SeriesId = "sr_2", SeriesTitle = "Example Series" });
        await WaitAsync(Waiting("ep_2"));
        var page = await PageAsync();
        bool armed = false;
        _h.Store.Changed += (_, _) =>
        {
            if (armed && _h.Store.WaitingIn(Scope7).Count == 0)
            {
                armed = false;
                _h.SwitchProfile("8");
            }
        };
        _confirmation.WhileAsking = async () =>
        {
            await _h.AddAsync("dl_3", DownloadState.Queued, mediaItemId: "ep_3", change: r => r with { SeriesId = "sr_1", SeriesTitle = "Example Series" });
            armed = true;
        };

        await page.Actions.DeleteSeriesCommand.ExecuteAsync(page.Series.Single(s => s.Card.Id == "sr_1").Card);

        Assert.Equal("8", _h.Session.ProfileId);
        // The other series' download stays: only sr_1's were read again.
        Assert.Equal(["dl_9"], _h.Store.In(Scope7).Select(d => d.Record.DownloadId));
        Assert.Equal(["dl_1", "dl_3"], _h.Store.PendingDeletes(Scope7).Select(d => d.DownloadId).Order(StringComparer.Ordinal));
    }

    // Its card and its season's Download count it as on its way: it is not offered again.
    [Fact]
    public async Task AWaitingEpisode_IsQueued_AndOpensTheDownloadsPage()
    {
        await WaitAsync(Waiting("ep_2"));
        var feature = _h.NewFeature();
        var manager = _h.NewManager(feature: feature);
        await manager.WaitingRun;
        var actions = new DownloadActions(manager, feature, new FakeDownloadOptionsDialog(), _confirmation, new RecordingNavigation(), new ImmediateDispatcher(),
            () => throw new InvalidOperationException("No dialog opens for a waiting episode."), _h.Logs.For<DownloadActions>());

        var state = actions.StateOf("ep_2");

        Assert.Equal((true, "Queued", true, false), (state.IsShown, state.Label, state.OpensDownloads, state.CanDelete));
        Assert.True(manager.IsWaiting("ep_2"));
        Assert.False(manager.IsWaiting("ep_3"));
    }
}
