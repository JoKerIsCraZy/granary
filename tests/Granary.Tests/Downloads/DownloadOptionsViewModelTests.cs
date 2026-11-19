using System.Net;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Browse;
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
        return NewOptions(feature, _h.Disk, new ImmediateDispatcher());
    }

    // The dialog over what the feature read, free space as the disk answers it.
    private DownloadOptionsViewModel NewOptions(DownloadsFeature feature, IDiskSpace disk, IUiDispatcher ui) =>
        new(_h.NewManager(feature: feature), feature, _h.Preferences, disk, ui, _h.Time, _h.Logs.For<DownloadOptionsViewModel>());

    private async Task<DownloadsFeature> ReadFeatureAsync()
    {
        var feature = _h.NewFeature();
        await feature.RefreshAsync(Token);
        return feature;
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
    public async Task InGerman_TheDialogSpeaksTheWebsWords_AndItsOwnWithGermanNumbers()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        _h.Disk.Set(1 * GB);

        var movie = await ForTitleAsync(Movie, [OneFile]);
        var preset = await ForTitleAsync(Movie, [new("4567", "2160p · HEVC", 40 * GB)], 7200, "original", "10mbps");
        preset.SelectedQuality = preset.Qualities[1];
        var season = await OptionsAsync();
        season.ForSeason(Season, "", 6 * GB);
        await season.SpaceRead;

        Assert.Equal("Wähle eine Datei zum Herunterladen. Achte darauf, dass genug Speicherplatz frei ist.", DownloadOptionsViewModel.Description);
        Assert.Equal("Download: Example Movie", movie.Title);
        Assert.Equal("Nicht genug freier Speicherplatz auf diesem PC: 8,0 GB benötigt, 1,0 GB frei.", movie.SpaceText);
        Assert.Equal("Etwa 9,2 GB", preset.SizeText);
        Assert.Equal(("Download: Example Series · Staffel 1", "3 Folgen · 6,0 GB"), (season.Title, season.SizeText));
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
    public async Task PastTheStorageLimitForDownloads_SaysSo_AndCannotStart()
    {
        _h.Disk.Set(200 * GB);
        await _h.Preferences.SetStorageLimitGbAsync(10);
        await _h.AddAsync("dl_1", DownloadState.Completed, size: 5 * GB, mediaItemId: "mv_9");

        var options = await ForTitleAsync(Movie, [OneFile]);

        Assert.False(options.CanStart);
        Assert.True(options.IsShortOfSpace);
        Assert.Equal("Over your storage limit for downloads: 8.0 GB needed, 5.0 GB left of 10.0 GB.", options.SpaceText);
        Assert.False(await options.StartAsync());
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Post, CreatePath));

        await _h.Preferences.SetStorageLimitGbAsync(20);
        var roomy = await ForTitleAsync(Movie, [OneFile]);

        Assert.True(roomy.CanStart);
        Assert.Equal("200.0 GB free on this PC.", roomy.SpaceText);
    }

    [Fact]
    public async Task Download_CountsWhatTheDownloadsTakeAsItIsPressed_NotAsTheDialogOpened()
    {
        _h.Disk.Set(200 * GB);
        await _h.Preferences.SetStorageLimitGbAsync(10);
        var options = await ForTitleAsync(Movie, [OneFile]);
        Assert.True(options.CanStart);

        // Downloads went on while the dialog showed.
        await _h.AddAsync("dl_1", DownloadState.Completed, size: 5 * GB, mediaItemId: "mv_9");

        Assert.False(await options.StartAsync());
        Assert.Equal("Over your storage limit for downloads: 8.0 GB needed, 5.0 GB left of 10.0 GB.", options.SpaceText);
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
        var options = NewOptions(await ReadFeatureAsync(), disk, ui);
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
        var options = NewOptions(await ReadFeatureAsync(), disk, new ImmediateDispatcher());
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
    public async Task AFreeSpaceReadThatFails_IsUnknown_AndDownloadStillStarts()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready")));
        var options = NewOptions(await ReadFeatureAsync(), new FailingDiskSpace(), new ImmediateDispatcher());

        options.ForTitle(Movie, [OneFile], 7200);
        await options.SpaceRead;

        Assert.Equal(("", false, true), (options.SpaceText, options.IsShortOfSpace, options.CanStart));
        Assert.True(await options.StartAsync());
        Assert.Single(CreateBodies());
        Assert.Contains(_h.Logs.Lines, line => line.Contains("free space where downloads go could not be read", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Logs.Lines, line => line.Contains(FailingDiskSpace.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFreeSpaceReadThatHangs_IsUnknownAfterFiveSeconds()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready")));
        // 1 GB would not hold the 8 GB file, if the drive ever answered.
        using var disk = new HeldDiskSpace(1 * GB);
        var options = NewOptions(await ReadFeatureAsync(), disk, new ImmediateDispatcher());
        options.ForTitle(Movie, [OneFile], 7200);

        var start = options.StartAsync();
        _h.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(start.IsCompleted);
        _h.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await start);
        Assert.Equal(("", true), (options.SpaceText, options.CanStart));
        Assert.Single(CreateBodies());
    }

    [Fact]
    public async Task ASeasonCutShortByAProfileSwitch_IsNotTakenAsStarted()
    {
        Creates(() =>
        {
            _h.Session.ProfileId = "8";
            return Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1"))();
        });
        _h.List();
        var options = await OptionsAsync();
        options.ForSeason(Season, "Season 1", 6 * GB);

        Assert.False(await options.StartAsync());

        Assert.Equal(DownloadOptionsViewModel.StartFailed, options.ErrorText);
        Assert.Single(CreateBodies());
    }

    [Fact]
    public async Task AStartThatFailsUnexpectedly_SaysSo_AndCanBeTriedAgain()
    {
        Creates(() => throw new InvalidOperationException("unexpected"), Created(DownloadPayloads.Entry("dl_1", "ready")));
        var options = await ForTitleAsync(Movie, [OneFile]);

        Assert.False(await options.StartAsync());
        Assert.Equal(DownloadOptionsViewModel.StartFailed, options.ErrorText);
        Assert.True(options.CanStart);

        Assert.True(await options.StartAsync());
        Assert.Equal("", options.ErrorText);
    }

    [Fact]
    public async Task AnotherChoice_ClearsTheLastStartsError()
    {
        Creates(ScriptedSilo.Problem(403, ProblemCodes.PermissionDenied));
        var options = await ForTitleAsync(Movie, [OneFile], presets: ["original", "10mbps"]);
        Assert.False(await options.StartAsync());
        Assert.Equal(DownloadOptionsViewModel.StartFailed, options.ErrorText);

        options.SelectedQuality = options.Qualities[1];

        Assert.Equal("", options.ErrorText);
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

    // Issue #166: the episodes past the server's limit wait on this PC, and the dialog says so as information, with Done.
    [Fact]
    public async Task TheServersDownloadLimit_KeepsTheRestWaiting_AndSaysSo()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1")), ScriptedSilo.Problem(429, ProblemCodes.RateLimited));
        var options = await OptionsAsync();
        options.ForSeason(Season, "Season 1", 6 * GB);
        Assert.Equal("Cancel", options.CloseText);

        Assert.False(await options.StartAsync());

        Assert.Equal(("", "Your Silo server's download limit is reached. 2 episodes wait on this PC and start once the server allows more.", "Done"),
            (options.ErrorText, options.NoticeText, options.CloseText));
        Assert.Equal(["ep_1", "ep_2"], CreateBodies().Select(b => b.GetProperty("episode_id").GetString()));
        Assert.Equal(["ep_2", "ep_3"], _h.Store.WaitingIn(Scope7).Select(w => w.EpisodeId));

        // Pressed again, it sends nothing: every episode is here or waits.
        Assert.True(await options.StartAsync());
        Assert.Equal(2, CreateBodies().Count);
    }

    [Fact]
    public async Task AFilmPastTheServersDownloadLimit_SaysSoInWordsThatNameIt()
    {
        Creates(ScriptedSilo.Problem(429, ProblemCodes.RateLimited));
        var options = await ForTitleAsync(Movie, [OneFile]);

        Assert.False(await options.StartAsync());

        Assert.Equal(("Your Silo server's download limit is reached. Try again later.", ""), (options.ErrorText, options.NoticeText));
        Assert.Equal(DownloadOptionsViewModel.LimitReached, options.ErrorText);
    }

    // A long season or series takes a while: Cancel stops it after the episode under way, whose download is kept, and the
    // dialog closes once that one ended.
    [Fact]
    public async Task CancelWhileASeasonStarts_StopsAfterTheEpisodeUnderWay_AndCloses()
    {
        var (held, gate) = ItemDetailFixture.Held();
        Creates(held);
        var options = await OptionsAsync();
        options.ForSeason(Season, "Season 1", 6 * GB);

        var start = options.StartAsync();
        await Until(() => CreateBodies().Count == 1, "the first episode's create was sent");
        options.StopStarting();
        gate.SetResult(await Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1"))());

        Assert.True(await start);
        Assert.Equal(("", false), (options.ErrorText, options.IsStarting));
        Assert.Equal(["ep_1"], CreateBodies().Select(b => b.GetProperty("episode_id").GetString()));
        Assert.NotNull(_h.Find("dl_1"));
    }
    // Issue #173: the dialog downloads for the profile it was opened in. Another profile active by the time Download is pressed
    // sends nothing, for a film as for a season, and the dialog says it did not start.
    [Fact]
    public async Task AProfileSwitchWhileATitlesDialogShows_SendsNoCreate()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready")));
        var options = await ForTitleAsync(Movie, [OneFile]);

        _h.SwitchProfile("8");

        Assert.False(await options.StartAsync());
        Assert.Empty(CreateBodies());
        Assert.Equal(DownloadOptionsViewModel.StartFailed, options.ErrorText);
        Assert.Empty(_h.Store.In(Scope8));
    }

    // Review of #179: a card's dialog whose read failed as the profile switched reads again with Try Again for the profile
    // active then, and downloads for that one: the profile goes with the offer on screen.
    [Fact]
    public async Task TryAgainAfterAProfileSwitch_DownloadsForTheProfileItReadFor()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready")));
        var options = await OptionsAsync();
        int reads = 0;
        options.ForReading(_ => ++reads == 1
            ? Task.FromException<DownloadOffer>(new ProfileChangedException())
            : Task.FromResult<DownloadOffer>(new TitleDownloadOffer(Movie, [OneFile], 7200)));
        await options.Reading;
        Assert.True(options.ReadFailed);

        _h.SwitchProfile("8");
        await options.TryAgainCommand.ExecuteAsync(null);
        await options.SpaceRead;

        Assert.True(await options.StartAsync());
        Assert.Equal("8", _h.Silo.SentTo(HttpMethod.Post, CreatePath).Single().AsProfile);
        Assert.NotNull(_h.Find("dl_1", Scope8));
    }

    [Fact]
    public async Task AProfileSwitchWhileASeasonsDialogShows_SendsNoCreate()
    {
        Creates(Created(DownloadPayloads.Entry("dl_1", "ready", episodeId: "ep_1")));
        var options = await OptionsAsync();
        options.ForSeason(Season, "Season 1", 6 * GB);
        await options.SpaceRead;

        _h.SwitchProfile("8");

        Assert.False(await options.StartAsync());
        Assert.Empty(CreateBodies());
        Assert.Empty(_h.Store.WaitingIn(Scope8));
    }
}
