using System.Globalization;
using System.Net;
using System.Text.Json;
using Granary.Core;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Downloads;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;
using Microsoft.Extensions.DependencyInjection;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The seek-bar previews kept with a download: fetched with its assets, as its own account and profile, the sheets without
/// credentials; kept whole or not at all, fetched again for a new revision or layout, and held to a cap.
/// </summary>
public sealed class DownloadPreviewsTests : IAsyncDisposable
{
    private const string TrickplayPath = "api/v2/watch/mv_1/trickplay";
    private const string FirstSheetPath = "api/v2/artwork/tp0";
    private static readonly Uri SecondSheet = new("https://cdn.example/sheets/tp1.jpg?sig=b");
    private static readonly byte[] SheetBytes = [0xFF, 0xD8, 0xFF, 0xE0, 9, 8, 7, 6];

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private DownloadAssets Assets(long maxPreviewBytes = DownloadAssets.MaxPreviewSetBytes) =>
        new(_h.Api, _h.Time, _h.Logs.For<DownloadAssets>(), new WatchApi(_h.Silo.Http), new FakeHttpClients(_h.Silo.Handler))
        {
            MaxPreviewBytes = maxPreviewBytes,
        };

    // The manifest and assets, then the previews, as the queue fetches them.
    private async Task<FetchedAssets> FetchAllAsync(StoredDownload download, long maxPreviewBytes = DownloadAssets.MaxPreviewSetBytes)
    {
        var assets = Assets(maxPreviewBytes);
        var fetched = await assets.FetchAsync(ScriptedSilo.Base, download, Token);
        await assets.FetchPreviewsAsync(ScriptedSilo.Base, download, Token);
        return fetched;
    }

    // A sheet host that takes the request and never answers, until the request is called off.
    private TaskCompletionSource StallFirstSheet()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.OnCancellable(HttpMethod.Get, FirstSheetPath, async cancellationToken =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Never answers.");
        });
        return asked;
    }

    private static string[] FilesIn(StoredDownload download) =>
        [.. Directory.EnumerateFiles(download.Folder).Select(Path.GetFileName).OfType<string>().Order()];

    // The file's previews: two sheets, one on the server and one on another host, both signed.
    private static string Previews(int columns = 10) => $$"""
        { "file_id": "4567", "interval_ms": 10000, "thumbnail_width": 320, "thumbnail_height": 180, "tile_columns": {{columns}},
          "tile_rows": 10, "thumbnail_count": 150, "sheets": [
            { "index": 1, "url": "{{SecondSheet}}" },
            { "index": 0, "url": "/{{FirstSheetPath}}?exp=1&sig=a" } ],
          "expires_at": "2026-10-01T15:00:00.000Z" }
        """;

    private void ServePreviews(string? previews = null)
    {
        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Json(HttpStatusCode.OK, previews ?? Previews()));
        _h.Silo.On(HttpMethod.Get, FirstSheetPath, DownloadPayloads.Asset(SheetBytes, "image/jpeg"));
        _h.Silo.On(HttpMethod.Get, SecondSheet, DownloadPayloads.Asset(SheetBytes, "image/jpeg"));
    }

    private int SheetRequests() => _h.Silo.SentTo(HttpMethod.Get, FirstSheetPath).Count
        + _h.Silo.Sent.Count(r => r.Uri.GetLeftPart(UriPartial.Path) == SecondSheet.GetLeftPart(UriPartial.Path));

    [Fact]
    public async Task ThePreviews_AreKeptInTheDownloadsFolder()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();

        await FetchAllAsync(stored);

        Assert.Contains(DownloadPaths.Previews, FilesIn(stored));
        Assert.Equal(SheetBytes, await File.ReadAllBytesAsync(Path.Combine(stored.Folder, SheetName(stored, 0)), Token));
        Assert.Equal(SheetBytes, await File.ReadAllBytesAsync(Path.Combine(stored.Folder, SheetName(stored, 1)), Token));
        Assert.DoesNotContain(FilesIn(stored), name => name.EndsWith(".arriving", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal));
        using var kept = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Previews), Token));
        Assert.Equal(1, kept.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal("4567", kept.RootElement.GetProperty("file_id").GetString());
        Assert.Equal(2, kept.RootElement.GetProperty("sheets").GetInt32());
    }

    [Fact]
    public async Task ThePreviews_AreReadAsTheDownloadsAccountAndProfile_AndTheSheetsWithoutCredentials()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();

        await FetchAllAsync(stored);

        var read = Assert.Single(_h.Silo.SentTo(HttpMethod.Get, TrickplayPath));
        Assert.Equal("?file_id=4567", read.Uri.Query);
        Assert.Equal(("1", "7"), (read.AsAccount, read.AsProfile));
        var sheets = _h.Silo.Sent.Where(r => r.Uri.AbsolutePath.EndsWith("tp0", StringComparison.Ordinal) || r.Uri.Host == SecondSheet.Host).ToList();
        Assert.Equal(2, sheets.Count);
        Assert.All(sheets, sheet => Assert.Equal((SiloAuth.Anonymous, (string?)null, (string?)null), (sheet.Auth, sheet.AsAccount, sheet.AsProfile)));
    }

    [Fact]
    public async Task TheManifestAndAssets_AreFetchedWithoutWaitingForPreviews()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();

        await Assets().FetchAsync(ScriptedSilo.Base, stored, Token);

        Assert.Empty(_h.Silo.SentTo(HttpMethod.Get, TrickplayPath));
    }

    // A sheet host that does not answer holds the previews a minute at most, all sheets together.
    [Fact]
    public async Task APreviewSetThatTakesTooLong_IsGivenUpAtItsDeadline()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        var asked = StallFirstSheet();
        var assets = Assets();
        await assets.FetchAsync(ScriptedSilo.Base, stored, Token);

        var previews = assets.FetchPreviewsAsync(ScriptedSilo.Base, stored, Token);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        _h.Time.Advance(DownloadAssets.PreviewDeadline - TimeSpan.FromSeconds(1));
        Assert.False(previews.IsCompleted);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await previews.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.DoesNotContain(FilesIn(stored), name => name == DownloadPaths.Previews || name.StartsWith("tp-", StringComparison.Ordinal));
        Assert.Contains(_h.Logs.Lines, line => line.Contains("were not kept (took too long)", StringComparison.Ordinal));
    }

    // The media's transfer does not wait for a sheet host that does not answer.
    [Fact]
    public async Task ASlowSheetHost_DoesNotHoldUpTheMediasTransfer()
    {
        await _h.AddAsync("dl_1");
        ServePreviews();
        var asked = StallFirstSheet();
        _h.ServeFile("dl_1", DownloadPayloads.Source(3000));
        var queue = _h.NewQueue(assets: Assets());
        queue.Start();

        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Until(() => _h.Find("dl_1")?.Record.State == DownloadState.Completed, "the media came");

        Assert.Single(_h.FileRequests("dl_1"));
    }

    [Theory]
    [InlineData(true)] // the host answers 503
    [InlineData(false)] // the connection fails
    public async Task AFailingSheet_LeavesNothingSignedInTheLog(bool answers)
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        _h.Silo.On(HttpMethod.Get, SecondSheet, answers
            ? () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            : ScriptedSilo.NetworkError());

        await FetchAllAsync(stored);

        Assert.Contains(_h.Logs.Lines, line => line.Contains("were not kept", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Logs.Lines, line => line.Contains("sig=", StringComparison.Ordinal) || line.Contains("cdn.example", StringComparison.Ordinal)
            || line.Contains("mv_1", StringComparison.Ordinal) || line.Contains("tp0", StringComparison.Ordinal));
    }

    // A body that names no length is held to the cap as it arrives, and leaves no piece behind.
    [Fact]
    public async Task ASheetWithoutALength_IsHeldToTheCapAsItArrives()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        _h.Silo.On(HttpMethod.Get, FirstSheetPath, DownloadPayloads.Streamed(HttpStatusCode.OK, new MemoryStream(DownloadPayloads.Source(64)), null, null));

        await FetchAllAsync(stored, maxPreviewBytes: 32);

        Assert.DoesNotContain(FilesIn(stored), name => name.StartsWith("tp-", StringComparison.Ordinal));
        Assert.Contains(_h.Logs.Lines, line => line.Contains("larger than 32 bytes", StringComparison.Ordinal));
    }

    // A play that began with the set kept before reads its sheets as it shows them: a new set leaves them be, and only the
    // generation before that goes.
    [Fact]
    public async Task ANewSet_KeepsTheSheetsOfTheSetBefore_AndDropsOlderOnes()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        string first = SheetName(stored, 0);
        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Json(HttpStatusCode.OK, Previews(columns: 20)));
        await FetchAllAsync(stored);
        string second = SheetName(stored, 0);

        Assert.Contains(first, FilesIn(stored));
        Assert.Contains(second, FilesIn(stored));

        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Json(HttpStatusCode.OK, Previews(columns: 30)));
        await FetchAllAsync(stored);

        Assert.DoesNotContain(first, FilesIn(stored));
        Assert.Contains(second, FilesIn(stored));
        Assert.Contains(SheetName(stored, 0), FilesIn(stored));
    }

    // Pieces of sheets a stopped fetch left go when the next set is kept.
    [Fact]
    public async Task PiecesOfSheetsLeftBehind_GoWhenASetIsKept()
    {
        var stored = await _h.AddAsync("dl_1");
        string piece = Path.Combine(stored.Folder, "tp-0a1b2c3d-0.jpg.0123456789abcdef.arriving");
        await File.WriteAllBytesAsync(piece, [1, 2, 3], Token);
        ServePreviews();

        await FetchAllAsync(stored);

        Assert.False(File.Exists(piece));
    }

    [Fact]
    public async Task NothingOfTheSheets_ReachesTheLog()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();

        await FetchAllAsync(stored);

        Assert.DoesNotContain(_h.Logs.Lines, line => line.Contains("sig=", StringComparison.Ordinal) || line.Contains("cdn.example", StringComparison.Ordinal)
            || line.Contains("mv_1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileWithoutPreviews_LeavesTheFolderWithoutThem()
    {
        var stored = await _h.AddAsync("dl_1");
        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Problem(404, "not_found"));

        var fetched = await FetchAllAsync(stored);

        Assert.Equal("poster.jpg", fetched.ArtworkFiles["poster"]);
        Assert.DoesNotContain(DownloadPaths.Previews, FilesIn(stored));
    }

    // A new set that does not come whole leaves the one kept before as it was, readable and with all its sheets.
    [Fact]
    public async Task ANewSetThatBreaksOff_LeavesTheSetKeptBeforeWhole()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        string[] before = FilesIn(stored);
        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Json(HttpStatusCode.OK, Previews(columns: 20)));
        _h.Silo.On(HttpMethod.Get, SecondSheet, () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        await FetchAllAsync(stored);

        Assert.Equal(before, FilesIn(stored));
        Assert.NotNull(KeptPreviews.Read(stored, ManifestOf(stored), _h.Logs.For<DownloadPreviewsTests>()));
    }

    // A play that read the old set goes on reading its own sheets: the new set's are other files.
    [Fact]
    public async Task ANewSet_NeverOverwritesTheSheetsOfTheOneBefore()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        string old = SheetName(stored, 0);
        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Json(HttpStatusCode.OK, Previews(columns: 20)));

        await FetchAllAsync(stored);

        Assert.NotEqual(old, SheetName(stored, 0));
        Assert.Contains(old, FilesIn(stored));
    }

    [Fact]
    public async Task AListWithAnEmptySheet_IsNotKept_AndTheRestOfTheFetchStands()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews(Previews().Replace("{ \"index\": 1, ", "null, { \"index\": 1, ", StringComparison.Ordinal));

        var fetched = await FetchAllAsync(stored);

        Assert.Equal("poster.jpg", fetched.ArtworkFiles["poster"]);
        Assert.DoesNotContain(FilesIn(stored), name => name == DownloadPaths.Previews || name.StartsWith("tp-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANotFound_KeepsASetOfTheSameBytes()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Problem(404, "not_found"));

        await FetchAllAsync(stored);

        Assert.Contains(DownloadPaths.Previews, FilesIn(stored));
        Assert.Contains(SheetName(stored, 1), FilesIn(stored));
    }

    // A replaced revision leaves the old set in the folder; new bytes without previews do not keep it.
    [Fact]
    public async Task ANotFound_ForANewRevision_DropsTheOldSet()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Problem(404, "not_found"));

        var renewed = await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { Revision = 2 }, Token);
        await FetchAllAsync(renewed);

        Assert.DoesNotContain(FilesIn(stored), name => name == DownloadPaths.Previews || name.StartsWith("tp-", StringComparison.Ordinal));
    }

    // The set is kept under the file it was asked for, whatever the answer names.
    [Fact]
    public async Task TheSet_IsKeptUnderTheFileAskedFor()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews(Previews().Replace("\"file_id\": \"4567\"", "\"file_id\": \"\"", StringComparison.Ordinal));

        await FetchAllAsync(stored);

        Assert.NotNull(KeptPreviews.Read(stored, ManifestOf(stored), _h.Logs.For<DownloadPreviewsTests>()));
    }

    [Theory]
    [InlineData(true)] // the previews' read
    [InlineData(false)] // a sheet
    public async Task AFailedFetch_LeavesTheDownloadAsItIs_AndIsTriedAgainAtTheNext(bool listFails)
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        if (listFails)
        {
            _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, Previews()));
        }
        else
        {
            _h.Silo.On(HttpMethod.Get, SecondSheet, () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                DownloadPayloads.Asset(SheetBytes, "image/jpeg"));
        }

        var fetched = await FetchAllAsync(stored);

        Assert.Equal("poster.jpg", fetched.ArtworkFiles["poster"]);
        Assert.Equal("r1", fetched.SubtitleRevisions["external:0"]);
        Assert.DoesNotContain(FilesIn(stored), name => name == DownloadPaths.Previews || name.StartsWith("tp-", StringComparison.Ordinal));

        await FetchAllAsync(stored);

        Assert.Contains(DownloadPaths.Previews, FilesIn(stored));
        Assert.Contains(SheetName(stored, 1), FilesIn(stored));
    }

    [Fact]
    public async Task AKeptSetTheServerStillHas_IsNotFetchedAgain()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        int sheets = SheetRequests();

        await FetchAllAsync(stored);

        Assert.Equal(2, sheets);
        Assert.Equal(sheets, SheetRequests());
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, TrickplayPath).Count);
    }

    [Fact]
    public async Task ANewRevision_FetchesTheSetAgain()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);

        var renewed = await _h.Store.UpdateAsync(Scope7, "dl_1", r => r with { Revision = 2 }, Token);
        await FetchAllAsync(renewed);

        Assert.Equal(4, SheetRequests());
        using var kept = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Previews), Token));
        Assert.Equal(2, kept.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task AnotherLayoutOnTheServer_FetchesTheSetAgain_WithOnlyTheSheetsItHas()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        string one = """
            { "file_id": "4567", "interval_ms": 10000, "thumbnail_width": 320, "thumbnail_height": 180, "tile_columns": 20,
              "tile_rows": 10, "thumbnail_count": 150, "sheets": [ { "index": 0, "url": "/api/v2/artwork/tp0?exp=2&sig=c" } ],
              "expires_at": "2026-10-01T15:00:00.000Z" }
            """;
        _h.Silo.On(HttpMethod.Get, TrickplayPath, ScriptedSilo.Json(HttpStatusCode.OK, one));

        await FetchAllAsync(stored);

        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Get, FirstSheetPath).Count);
        Assert.Contains(SheetName(stored, 0), FilesIn(stored));
        Assert.Single(KeptPreviews.Read(stored, ManifestOf(stored), _h.Logs.For<DownloadPreviewsTests>())!.SheetFiles!);
        using var kept = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Previews), Token));
        Assert.Equal(20, kept.RootElement.GetProperty("tile_columns").GetInt32());
    }

    [Fact]
    public async Task ASetLargerThanTheCap_IsNotKept_AndNotFetchedAgainWhileItStaysSo()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();

        // Each sheet alone fits; both do not.
        await FetchAllAsync(stored, SheetBytes.Length + 1);

        Assert.DoesNotContain(FilesIn(stored), name => name.StartsWith("tp-", StringComparison.Ordinal) || name.EndsWith(".arriving", StringComparison.Ordinal));
        Assert.Null(KeptPreviews.Read(stored, ManifestOf(stored), _h.Logs.For<DownloadPreviewsTests>()));
        int sheets = SheetRequests();
        Assert.Contains(_h.Logs.Lines, line => line.Contains("larger than", StringComparison.Ordinal));

        await FetchAllAsync(stored, SheetBytes.Length + 1);

        Assert.Equal(sheets, SheetRequests());
    }

    [Fact]
    public async Task ASheetThatSaysItIsLargerThanTheCap_IsNotRead()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();

        await FetchAllAsync(stored, 4);

        Assert.Equal(1, SheetRequests());
        Assert.DoesNotContain(FilesIn(stored), name => name.StartsWith("tp-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAWatchApi_NoPreviewsAreAskedFor()
    {
        var stored = await _h.AddAsync("dl_1");

        await new DownloadAssets(_h.Api, _h.Time, _h.Logs.For<DownloadAssets>()).FetchAsync(ScriptedSilo.Base, stored, Token);

        Assert.Empty(_h.Silo.SentTo(HttpMethod.Get, TrickplayPath));
    }

    [Fact]
    public void TheAppsAssets_KeepThePreviews()
    {
        using var provider = new ServiceCollection().AddLogging().AddGranaryCore().BuildServiceProvider();

        Assert.True(provider.GetRequiredService<DownloadAssets>().KeepsPreviews);
    }

    [Fact]
    public async Task TheKeptSet_ReadsAsLocalSheets()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);

        var trickplay = KeptPreviews.Read(stored, ManifestOf(stored), _h.Logs.For<DownloadPreviewsTests>());

        Assert.NotNull(trickplay);
        Assert.Equal(("4567", 10_000, 320, 180, 10, 10, 150), (trickplay.FileId, trickplay.IntervalMs, trickplay.Width, trickplay.Height, trickplay.Columns,
            trickplay.Rows, trickplay.Count));
        Assert.Equal([Path.Combine(stored.Folder, SheetName(stored, 0)), Path.Combine(stored.Folder, SheetName(stored, 1))], trickplay.SheetFiles);
        Assert.Equal(1, trickplay.TileAt(1000)!.Value.Sheet);
        Assert.Equal(DateTimeOffset.MaxValue, trickplay.ExpiresAt);
    }

    [Theory]
    [InlineData("revision", "2")] // the media's bytes are another revision's
    [InlineData("file_id", "\"9999\"")] // another file of the title
    [InlineData("sheets", "3")] // a sheet that is not there
    [InlineData("tile_columns", "0")]
    [InlineData("tile_rows", "101")] // more than a sheet holds
    [InlineData("thumbnail_width", "4097")] // wider than a thumbnail is
    [InlineData("generation", "\"..\\\\x\"")] // not a name Granary makes
    public async Task AKeptSetThatDoesNotFitTheDownload_IsNotShown(string field, string value)
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        string path = Path.Combine(stored.Folder, DownloadPaths.Previews);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        json[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        await File.WriteAllTextAsync(path, json.ToJsonString(), Token);

        Assert.Null(KeptPreviews.Read(stored, ManifestOf(stored), _h.Logs.For<DownloadPreviewsTests>()));
    }

    // 65536 by 65536 tiles count to 0 in an int, which the seek bar would divide by.
    [Fact]
    public async Task AKeptSetWhoseGridAnIntCannotCount_IsNotShown()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        string path = Path.Combine(stored.Folder, DownloadPaths.Previews);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        json["tile_columns"] = 65536;
        json["tile_rows"] = 65536;
        await File.WriteAllTextAsync(path, json.ToJsonString(), Token);

        Assert.Null(KeptPreviews.Read(stored, ManifestOf(stored), _h.Logs.For<DownloadPreviewsTests>()));
    }

    [Fact]
    public async Task ABrokenKeptSet_IsNotShown()
    {
        var stored = await _h.AddAsync("dl_1");
        await File.WriteAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Previews), "{ not json", Token);
        await File.WriteAllTextAsync(Path.Combine(stored.Folder, DownloadPaths.Manifest), DownloadPayloads.Manifest("dl_1"), Token);

        Assert.Null(KeptPreviews.Read(stored, ManifestOf(stored), _h.Logs.For<DownloadPreviewsTests>()));
    }

    [Fact]
    public async Task Removing_TheDownload_RemovesItsPreviews()
    {
        var stored = await _h.AddAsync("dl_1");
        ServePreviews();
        await FetchAllAsync(stored);
        Assert.Contains(SheetName(stored, 0), FilesIn(stored));

        await _h.Store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token);

        Assert.False(Directory.Exists(stored.Folder));
    }

    [Fact]
    public async Task TheSpaceDownloadsTake_CountsThePreviews()
    {
        var stored = await _h.AddAsync("dl_1");
        long before = _h.Store.BytesOnDisk(Scope7);
        ServePreviews();

        await FetchAllAsync(stored);

        long kept = new FileInfo(Path.Combine(stored.Folder, DownloadPaths.Previews)).Length;
        Assert.True(_h.Store.BytesOnDisk(Scope7) >= before + (2 * SheetBytes.Length) + kept,
            string.Create(CultureInfo.InvariantCulture, $"{_h.Store.BytesOnDisk(Scope7)} after {before}"));
    }

    private static Granary.Core.Api.Downloads.OfflineManifest ManifestOf(StoredDownload download) =>
        Granary.Core.Api.Downloads.OfflineManifest.Read(File.ReadAllBytes(Path.Combine(download.Folder, DownloadPaths.Manifest)));

    // The name of the kept set's sheet at ordinal, after the generation its description names.
    private static string SheetName(StoredDownload download, int ordinal)
    {
        using var kept = JsonDocument.Parse(File.ReadAllText(Path.Combine(download.Folder, DownloadPaths.Previews)));
        return DownloadPaths.PreviewSheet(kept.RootElement.GetProperty("generation").GetString()!, ordinal);
    }
}
