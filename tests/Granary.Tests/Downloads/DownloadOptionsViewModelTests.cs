using System.Net;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The download dialog: the web's version picker with its title and texts, the qualities the server offers with the apps'
/// labels, the size, and the free space where downloads go, read off the UI thread.
/// </summary>
public sealed class DownloadOptionsViewModelTests : IAsyncDisposable
{
    private const string CreatePath = "api/v2/downloads";
    private const long GB = 1L << 30;

    private static readonly DownloadRequest Movie = new("mv_1", null, "4567", DownloadQualities.Original, "Example Movie", null, null, null);
    private static readonly DownloadRequest Episode = new("ep_5", "sr_1", "4567", DownloadQualities.Original, "Chapter Five", "Example Series", 1, 5);
    private static readonly DownloadVersionRow OneFile = new("4567", "1080p · H264 · EAC3", 8 * GB);

    private static readonly SeasonDownloadRequest Season = new("sr_1", "Example Series", 1,
        [new("ep_1", 1, "Chapter One"), new("ep_2", 2, "Chapter Two"), new("ep_3", 3, "Chapter Three")], DownloadQualities.Original);

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    // The dialog over what the server offers: the presets given, or Original alone.
    private async Task<DownloadOptionsViewModel> OptionsAsync(params string[] presets)
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(true, true, presets)));
        var feature = _h.NewFeature();
        await feature.RefreshAsync(Token);
        return new DownloadOptionsViewModel(_h.NewManager(feature: feature), feature, _h.Preferences, _h.Disk, new ImmediateDispatcher());
    }

    private async Task<DownloadOptionsViewModel> ForTitleAsync(DownloadRequest request, IReadOnlyList<DownloadVersionRow> versions,
        int runtimeSeconds = 7200, params string[] presets)
    {
        var options = await OptionsAsync(presets);
        options.ForTitle(request, versions, runtimeSeconds);
        await options.SpaceRead;
        return options;
    }

    private void Creates(params Func<Task<HttpResponseMessage>>[] answers) => _h.Silo.On(HttpMethod.Post, CreatePath, answers);

    private static Func<Task<HttpResponseMessage>> Created(string entry) =>
        ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, entry));

    private List<JsonElement> CreateBodies() =>
        [.. _h.Silo.SentTo(HttpMethod.Post, CreatePath).Select(r => JsonDocument.Parse(r.Body!).RootElement)];

    [Fact]
    public async Task Title_IsTheWebsDownloadColonTitle()
    {
        var movie = await ForTitleAsync(Movie, [OneFile]);
        var episode = await ForTitleAsync(Episode, [OneFile]);
        var season = await OptionsAsync();
        season.ForSeason(Season, "Season 1", 6 * GB);
        await season.SpaceRead;

        Assert.Equal("Download: Example Movie", movie.Title);
        Assert.Equal("Download: Example Series · S1 · E5", episode.Title);
        Assert.Equal("Download: Example Series · Season 1", season.Title);
        Assert.Equal("Choose a file to download. Make sure you have enough disk space.", DownloadOptionsViewModel.Description);
        Assert.Equal("Larger files require more storage space.", DownloadOptionsViewModel.Footer);
    }

    [Fact]
    public async Task OneFile_ShowsNoVersionRow()
    {
        var options = await ForTitleAsync(Movie, [OneFile]);

        Assert.False(options.ShowsVersions);
        Assert.False(options.ShowsFooter);
        Assert.Equal("4567", options.SelectedVersion?.FileId);
        Assert.Equal("8.0 GB", options.SizeText);
    }

    [Fact]
    public async Task TwoFiles_ListTheVersions_ThePickedOneSelected_AndItsIdIsSent()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready")));
        var options = await ForTitleAsync(Movie with { MediaFileId = "4568" },
            [new("4567", "2160p · HEVC · HDR10 · DTS", 40 * GB), new("4568", "1080p · H264 · DTS", 8 * GB)]);

        Assert.True(options.ShowsVersions);
        Assert.True(options.ShowsFooter);
        Assert.Equal(["40.0 GB", "8.0 GB"], options.Versions.Select(v => v.SizeText));
        Assert.Equal("4568", options.SelectedVersion?.FileId);

        Assert.True(await options.StartAsync());

        var body = Assert.Single(CreateBodies());
        Assert.Equal(("mv_1", "4568", "original"), (body.GetProperty("content_id").GetString(), body.GetProperty("media_file_id").GetString(),
            body.GetProperty("quality").GetString()));
        Assert.Equal("", options.ErrorText);
    }

    [Fact]
    public async Task AnotherVersion_IsTheOneSent()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready")));
        var options = await ForTitleAsync(Movie, [new("4567", "2160p · HEVC", 40 * GB), new("4568", "1080p · H264", 8 * GB)]);

        options.SelectedVersion = options.Versions[1];

        Assert.Equal("8.0 GB", options.SizeText);
        Assert.True(await options.StartAsync());
        Assert.Equal("4568", Assert.Single(CreateBodies()).GetProperty("media_file_id").GetString());
    }

    [Fact]
    public async Task Qualities_ShowWhatTheServerOffers_WithTheAppsLabels()
    {
        var two = await ForTitleAsync(Movie, [OneFile], presets: ["original", "10mbps"]);
        var one = await ForTitleAsync(Movie, [OneFile], presets: ["original"]);

        Assert.Equal([("original", "Original"), ("10mbps", "10 Mbps · up to 1080p")], two.Qualities.Select(q => (q.Preset, q.Label)));
        Assert.True(two.ShowsQualities);
        Assert.Equal(["Original"], one.Qualities.Select(q => q.Label));
        Assert.False(one.ShowsQualities);
    }

    [Fact]
    public async Task TheDefaultQuality_IsPreselected_WhenTheServerOffersIt()
    {
        await _h.Preferences.SetDefaultQualityAsync("5mbps");

        var offered = await ForTitleAsync(Movie, [OneFile], presets: ["original", "10mbps", "5mbps"]);
        var notOffered = await ForTitleAsync(Movie, [OneFile], presets: ["original", "10mbps"]);

        Assert.Equal("5mbps", offered.SelectedQuality?.Preset);
        Assert.Equal("Original", notOffered.SelectedQuality?.Label);
    }

    [Fact]
    public async Task APreset_ShowsAboutItsSize_NeverAboveTheSource()
    {
        // 10 Mbps for two hours: 9.0 GB of video and a tenth more for the rest, 9.9 GB, of a 40 GB source.
        var large = await ForTitleAsync(Movie, [new("4567", "2160p · HEVC", 40 * GB)], 7200, "original", "10mbps");
        var small = await ForTitleAsync(Movie, [new("4567", "720p · H264", 2 * GB)], 7200, "original", "10mbps");

        large.SelectedQuality = large.Qualities[1];
        small.SelectedQuality = small.Qualities[1];

        Assert.Equal("About 9.2 GB", large.SizeText);
        Assert.Equal("About 2.0 GB", small.SizeText);
        large.SelectedQuality = large.Qualities[0];
        Assert.Equal("40.0 GB", large.SizeText);
    }

    [Fact]
    public async Task NotEnoughSpace_SaysSo_AndCannotStart()
    {
        _h.Disk.Set(1 * GB);

        var options = await ForTitleAsync(Movie, [OneFile]);

        Assert.False(options.CanStart);
        Assert.Equal("Not enough free space on this PC: 8.0 GB needed, 1.0 GB free.", options.SpaceText);
        Assert.False(await options.StartAsync());
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Post, CreatePath));
    }

    [Fact]
    public async Task EnoughSpace_SaysWhatIsFree()
    {
        _h.Disk.Set(200 * GB);

        var options = await ForTitleAsync(Movie, [OneFile]);

        Assert.True(options.CanStart);
        Assert.Equal("200.0 GB free on this PC.", options.SpaceText);
        Assert.Equal([_h.Preferences.Folder], _h.Disk.Asked);
    }

    [Fact]
    public async Task ASmallerQuality_CanStartWhereTheOriginalCannot()
    {
        _h.Disk.Set(4 * GB);
        var options = await ForTitleAsync(Movie, [new("4567", "2160p · HEVC", 40 * GB)], 3600, "original", "5mbps");
        Assert.False(options.CanStart);

        options.SelectedQuality = options.Qualities[1];

        Assert.True(options.CanStart);
        Assert.Equal("4.0 GB free on this PC.", options.SpaceText);
        Assert.Single(_h.Disk.Asked);
    }

    [Fact]
    public async Task TheFreeSpace_IsReadOffTheUiThread_AndShownOnIt()
    {
        using var ui = new SerialDispatcher();
        var disk = new ThreadNotingDiskSpace(ui, 200 * GB);
        var feature = _h.NewFeature();
        await feature.RefreshAsync(Token);
        var options = new DownloadOptionsViewModel(_h.NewManager(feature: feature), feature, _h.Preferences, disk, ui);
        var shownOnUi = new List<bool>();
        options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadOptionsViewModel.SpaceText))
            {
                shownOnUi.Add(ui.HasThreadAccess);
            }
        };

        await ui.RunAsync(() =>
        {
            options.ForTitle(Movie, [OneFile], 7200);
            return options.SpaceRead;
        });

        Assert.Equal([false], disk.ReadOnUi);
        Assert.Equal("200.0 GB free on this PC.", ui.Read(() => options.SpaceText));
        Assert.NotEmpty(shownOnUi);
        Assert.All(shownOnUi, Assert.True);
    }

    [Fact]
    public async Task AStartBeforeTheSpaceIsKnown_WaitsForIt()
    {
        using var disk = new HeldDiskSpace(1 * GB);
        var feature = _h.NewFeature();
        await feature.RefreshAsync(Token);
        var options = new DownloadOptionsViewModel(_h.NewManager(feature: feature), feature, _h.Preferences, disk, new ImmediateDispatcher());
        options.ForTitle(Movie, [OneFile], 7200);
        Assert.True(options.CanStart);

        var start = options.StartAsync();
        Assert.False(start.IsCompleted);
        disk.Release();

        Assert.False(await start);
        Assert.Equal("Not enough free space on this PC: 8.0 GB needed, 1.0 GB free.", options.SpaceText);
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Post, CreatePath));
    }

    [Fact]
    public async Task AFailedStart_ShowsTheWebsError()
    {
        Creates(ScriptedSilo.Problem(403, ProblemCodes.PermissionDenied));
        var options = await ForTitleAsync(Movie, [OneFile]);

        Assert.False(await options.StartAsync());

        Assert.Equal("Download could not be started. Check access and try again.", options.ErrorText);
        Assert.Equal(DownloadOptionsViewModel.StartFailed, options.ErrorText);
        Assert.True(options.CanStart);
    }

    [Fact]
    public async Task ASeason_SaysHowManyEpisodes_AndStartsEachInTheChosenQuality()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1")), Created(DownloadPayloads.Entry("dl_2", "ready", episodeId: "ep_2")),
            Created(DownloadPayloads.Entry("dl_3", "ready", episodeId: "ep_3")));
        var options = await OptionsAsync("original", "10mbps");
        options.ForSeason(Season, "Season 1", 6 * GB);
        await options.SpaceRead;

        Assert.Equal("3 episodes · 6.0 GB", options.SizeText);
        Assert.False(options.ShowsVersions);
        Assert.False(options.ShowsFooter);
        options.SelectedQuality = options.Qualities[1];
        Assert.Equal("3 episodes", options.SizeText);

        Assert.True(await options.StartAsync());

        var bodies = CreateBodies();
        Assert.Equal(["ep_1", "ep_2", "ep_3"], bodies.Select(b => b.GetProperty("episode_id").GetString()));
        Assert.All(bodies, b => Assert.Equal(("sr_1", "10mbps"), (b.GetProperty("content_id").GetString(), b.GetProperty("quality").GetString())));
    }

    [Fact]
    public async Task ASeasonWithAnEpisodeThatFailed_SaysSo_AndTheNextPressSendsOnlyThatOne()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1")), ScriptedSilo.Problem(500, ProblemCodes.Unknown),
            Created(DownloadPayloads.Entry("dl_3", "ready", episodeId: "ep_3")));
        _h.List();
        var options = await OptionsAsync();
        options.ForSeason(Season, "Season 1", 6 * GB);

        Assert.False(await options.StartAsync());
        Assert.Equal(DownloadOptionsViewModel.StartFailed, options.ErrorText);

        Creates(Created(DownloadPayloads.Entry("dl_2", "ready", episodeId: "ep_2")));
        Assert.True(await options.StartAsync());
        Assert.Equal(["ep_1", "ep_2", "ep_3", "ep_2"], CreateBodies().Select(b => b.GetProperty("episode_id").GetString()));
        Assert.Equal("", options.ErrorText);
    }

    // Notes whether each read of the free space ran on the UI thread.
    private sealed class ThreadNotingDiskSpace(SerialDispatcher ui, long free) : IDiskSpace
    {
        private readonly Lock _gate = new();
        private readonly List<bool> _readOnUi = [];

        public IReadOnlyList<bool> ReadOnUi
        {
            get
            {
                lock (_gate)
                {
                    return [.. _readOnUi];
                }
            }
        }

        public long? FreeBytes(string folder)
        {
            lock (_gate)
            {
                _readOnUi.Add(ui.HasThreadAccess);
            }
            return free;
        }
    }

    // A drive that answers only once the test lets it, as one waking up does.
    private sealed class HeldDiskSpace(long free) : IDiskSpace, IDisposable
    {
        private readonly ManualResetEventSlim _released = new();

        public void Release() => _released.Set();

        public long? FreeBytes(string folder)
        {
            _released.Wait(TimeSpan.FromSeconds(5));
            return free;
        }

        public void Dispose() => _released.Dispose();
    }
}
