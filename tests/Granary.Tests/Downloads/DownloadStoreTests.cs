using System.Text.Json;
using System.Text.Json.Nodes;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Tests.Fakes;

namespace Granary.Tests.Downloads;

/// <summary>What is on this PC: one folder per download under its server, account and profile, and the index that lists them.</summary>
public sealed class DownloadStoreTests : IDisposable
{
    private static readonly DownloadScope Scope7 = new("srv1", "1", "7");
    private static readonly DownloadScope Scope8 = new("srv1", "1", "8");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly InMemoryDownloadPreferences _preferences;

    public DownloadStoreTests()
    {
        _preferences = new InMemoryDownloadPreferences(Root);
    }

    private string Root => Path.Combine(_directory, "downloads");

    private string IndexPath => Path.Combine(_directory, "data", "downloads.json");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private DownloadStore Create() => new(IndexPath, _preferences, _time, _logs.For<DownloadStore>());

    private async Task<DownloadStore> LoadedAsync()
    {
        var store = Create();
        await store.LoadAsync(Token);
        return store;
    }

    private static DownloadRecord Record(string id = "dl_1", DownloadScope? scope = null) => new()
    {
        DownloadId = id,
        Scope = scope ?? Scope7,
        MediaItemId = "ep_5",
        SeriesId = "sr_1",
        Title = "Chapter Five",
        SeriesTitle = "Example Series",
        SeasonNumber = 1,
        EpisodeNumber = 5,
        MediaFileId = "4567",
        Quality = DownloadQualities.Original,
        Revision = 1,
        ExpectedBytes = 3000,
        State = DownloadState.Downloading,
        Validator = "\"v1\"",
        PreparationProgress = 0.42,
        PendingReport = new DownloadStatusEvent(DownloadStatuses.Downloading, ManualTimeProvider.DefaultStart, 1),
        CreatedAt = ManualTimeProvider.DefaultStart,
        ManifestReadAt = ManualTimeProvider.DefaultStart.AddMinutes(1),
        SubtitleRevisions = new Dictionary<string, string> { ["external:0"] = "r1" },
    };

    [Fact]
    public async Task AnAddedDownload_LivesInItsOwnFolder_AndTheIndexSaysWhere()
    {
        var store = await LoadedAsync();

        var added = await store.AddAsync(Record(), Token);

        string folder = Path.Combine(Root, "srv1", "acct-1", "7", "dl_1");
        Assert.Equal(folder, added.Folder);
        Assert.False(added.FilesMissing);
        Assert.Equal(Path.Combine(folder, DownloadPaths.MediaPart), added.PartPath);
        Assert.Null(added.MediaPath);
        Assert.True(File.Exists(Path.Combine(folder, DownloadPaths.Record)));
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(IndexPath, Token));
        var entry = Assert.Single(index.RootElement.GetProperty("downloads").EnumerateArray());
        Assert.Equal(folder, entry.GetProperty("folder").GetString());
        Assert.Equal("dl_1", entry.GetProperty("download_id").GetString());
        Assert.Equal("Chapter Five", entry.GetProperty("title").GetString());
        Assert.Same(added, Assert.Single(store.In(Scope7)));
    }

    [Fact]
    public async Task AtTheNextStart_EveryDownloadIsBack()
    {
        var record = Record() with
        {
            MediaFile = "media.mkv",
            State = DownloadState.Completed,
            Failure = DownloadFailure.None,
            IsPausedByViewer = true,
            ArtworkFiles = new Dictionary<string, string> { ["poster"] = "poster.jpg", ["series_poster"] = "series_poster.img" },
        };
        await (await LoadedAsync()).AddAsync(record, Token);

        var again = await LoadedAsync();

        var back = Assert.Single(again.All);
        Assert.False(back.FilesMissing);
        Assert.Equal(record, back.Record with { SubtitleRevisions = record.SubtitleRevisions, ArtworkFiles = record.ArtworkFiles });
        Assert.Equal(record.SubtitleRevisions, back.Record.SubtitleRevisions);
        Assert.Equal(record.ArtworkFiles, back.Record.ArtworkFiles);
        Assert.Equal(Path.Combine(back.Folder, "media.mkv"), back.MediaPath);
        Assert.Same(back, again.Find(Scope7, "dl_1"));
        Assert.Same(back, again.FindByItem(Scope7, "ep_5"));
    }

    [Fact]
    public async Task AFolderThatIsGone_KeepsItsRow_AsMissingFiles()
    {
        var added = await (await LoadedAsync()).AddAsync(Record(), Token);
        string aside = added.Folder + ".unplugged";
        Directory.Move(added.Folder, aside);

        var store = await LoadedAsync();

        var missing = Assert.Single(store.In(Scope7));
        Assert.True(missing.FilesMissing);
        Assert.Equal(("dl_1", "ep_5", "Chapter Five", "Example Series", 1, 5),
            (missing.Record.DownloadId, missing.Record.MediaItemId, missing.Record.Title, missing.Record.SeriesTitle, missing.Record.SeasonNumber,
                missing.Record.EpisodeNumber));
        int changes = 0;
        store.Changed += (_, _) => changes++;

        Directory.Move(aside, added.Folder);
        store.RecheckFolders();

        var back = Assert.Single(store.In(Scope7));
        Assert.False(back.FilesMissing);
        Assert.Equal("\"v1\"", back.Record.Validator);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task AnotherProfilesDownloads_AreNotListed()
    {
        var store = await LoadedAsync();
        await store.AddAsync(Record("dl_1", Scope7), Token);
        await store.AddAsync(Record("dl_2", Scope8), Token);

        Assert.Equal(["dl_1"], store.In(Scope7).Select(d => d.Record.DownloadId));
        Assert.Equal(["dl_2"], store.In(Scope8).Select(d => d.Record.DownloadId));
        Assert.Null(store.Find(Scope7, "dl_2"));
        Assert.Equal(2, store.All.Count);
    }

    [Fact]
    public async Task Remove_DeletesTheFolder_AndQueuesTheServerDelete()
    {
        var store = await LoadedAsync();
        var added = await store.AddAsync(Record(), Token);
        await File.WriteAllBytesAsync(added.PartPath, new byte[100], Token);
        int changes = 0;
        store.Changed += (_, _) => changes++;

        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token);

        Assert.False(Directory.Exists(added.Folder));
        Assert.Empty(store.In(Scope7));
        Assert.Equal([new PendingDelete(Scope7, "dl_1")], store.PendingDeletes(Scope7));
        Assert.Empty(store.PendingDeletes(Scope8));
        Assert.True(changes > 0);

        var again = await LoadedAsync();
        Assert.Empty(again.All);
        Assert.Equal([new PendingDelete(Scope7, "dl_1")], again.PendingDeletes(Scope7));

        await again.ForgetPendingDeleteAsync(new PendingDelete(Scope7, "dl_1"), Token);

        Assert.Empty(again.PendingDeletes(Scope7));
        Assert.Empty((await LoadedAsync()).PendingDeletes(Scope7));
    }

    [Fact]
    public async Task ANewFolder_AppliesToNewDownloadsOnly()
    {
        var store = await LoadedAsync();
        var old = await store.AddAsync(Record("dl_1"), Token);
        string newRoot = Path.Combine(_directory, "other-drive", "Granary");

        await _preferences.SetFolderAsync(newRoot);
        var created = await store.AddAsync(Record("dl_2"), Token);

        Assert.StartsWith(Root, old.Folder, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(newRoot, "srv1", "acct-1", "7", "dl_2"), created.Folder);
        var again = await LoadedAsync();
        Assert.Equal(old.Folder, again.Find(Scope7, "dl_1")!.Folder);
        Assert.Equal(created.Folder, again.Find(Scope7, "dl_2")!.Folder);
        Assert.All(again.All, d => Assert.False(d.FilesMissing));
    }

    [Fact]
    public async Task AnIndexThatCannotBeRead_IsRebuiltFromTheFolders()
    {
        var store = await LoadedAsync();
        await store.AddAsync(Record("dl_1", Scope7), Token);
        await store.AddAsync(Record("dl_2", Scope8), Token);
        await File.WriteAllTextAsync(IndexPath, "{", Token);

        var rebuilt = await LoadedAsync();

        Assert.Equal(["dl_1", "dl_2"], rebuilt.All.Select(d => d.Record.DownloadId).Order(StringComparer.Ordinal));
        Assert.All(rebuilt.All, d => Assert.False(d.FilesMissing));
        Assert.Equal(["dl_1", "dl_2"], (await LoadedAsync()).All.Select(d => d.Record.DownloadId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARecord_KeepsSizesOver4GB()
    {
        await (await LoadedAsync()).AddAsync(Record() with { ExpectedBytes = 8_589_934_592 }, Token);

        Assert.Equal(8_589_934_592L, Assert.Single((await LoadedAsync()).All).Record.ExpectedBytes);
    }

    [Fact]
    public async Task Update_ChangesTheRecord_AndKeepsItAtTheNextStart()
    {
        var store = await LoadedAsync();
        await store.AddAsync(Record(), Token);
        int changes = 0;
        store.Changed += (_, _) => changes++;

        var updated = await store.UpdateAsync(Scope7, "dl_1", r => r with { State = DownloadState.Failed, Failure = DownloadFailure.NotEnoughSpace }, Token);

        Assert.Equal((DownloadState.Failed, DownloadFailure.NotEnoughSpace), (updated.Record.State, updated.Record.Failure));
        Assert.Equal(1, changes);
        var back = Assert.Single((await LoadedAsync()).All).Record;
        Assert.Equal((DownloadState.Failed, DownloadFailure.NotEnoughSpace), (back.State, back.Failure));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.UpdateAsync(Scope8, "dl_1", r => r, Token));
    }

    [Fact]
    public async Task RemoveWhere_TakesEveryMatchingDownloadAndPendingDelete()
    {
        var store = await LoadedAsync();
        var kept = await store.AddAsync(Record("dl_1", Scope7), Token);
        var gone = await store.AddAsync(Record("dl_2", Scope8), Token);
        await store.AddAsync(Record("dl_3", Scope8), Token);
        await store.RemoveAsync(Scope8, "dl_3", queueServerDelete: true, Token);

        await store.RemoveWhereAsync(scope => scope.ProfileId == "8", Token);

        Assert.Equal([kept], store.All);
        Assert.False(Directory.Exists(gone.Folder));
        Assert.Empty(store.PendingDeletes(Scope8));
    }

    [Fact]
    public async Task BytesOnDisk_CountsTheProfilesFiles()
    {
        var store = await LoadedAsync();
        var mine = await store.AddAsync(Record("dl_1", Scope7), Token);
        var other = await store.AddAsync(Record("dl_2", Scope8), Token);
        long recordBytes = new FileInfo(Path.Combine(mine.Folder, DownloadPaths.Record)).Length;
        await File.WriteAllBytesAsync(mine.PartPath, new byte[1000], Token);
        await File.WriteAllBytesAsync(other.PartPath, new byte[5000], Token);

        Assert.Equal(1000 + recordBytes, store.BytesOnDisk(Scope7));
    }

    [Fact]
    public async Task DamagedIndexEntries_AreLeftOut_AndTheRestLoad()
    {
        var kept = await (await LoadedAsync()).AddAsync(Record("dl_1"), Token);
        string elsewhere = Path.Combine(_directory, "elsewhere", "dl_9");
        Directory.CreateDirectory(elsewhere);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(IndexPath, Token))!;
        var downloads = index["downloads"]!.AsArray();
        var valid = downloads[0]!;
        downloads.Add(Entry(valid, e => e["scope"] = null));
        downloads.Add(Entry(valid, e => { e["download_id"] = "dl_2"; e["folder"] = null; }));
        downloads.Add(Entry(valid, e => { e["download_id"] = "dl_3"; e["title"] = null; e["media_item_id"] = null; }));
        downloads.Add(Entry(valid, e => { e["download_id"] = "dl_9"; e["folder"] = elsewhere; }));
        downloads.Add(Entry(valid, e => { e["download_id"] = "dl_4"; e["folder"] = "C:\\bad\0path"; }));
        index["pending_deletes"] = new JsonArray(new JsonObject { ["scope"] = null, ["download_id"] = "dl_5" });
        await File.WriteAllTextAsync(IndexPath, index.ToJsonString(), Token);

        var store = await LoadedAsync();

        Assert.Equal([kept.Folder], store.All.Select(d => d.Folder));
        Assert.Empty(store.PendingDeletes(Scope7));
        Assert.Contains("damaged", _logs.All, StringComparison.Ordinal);
        await store.RemoveWhereAsync(_ => true, Token);
        Assert.True(Directory.Exists(elsewhere));
    }

    [Fact]
    public async Task AFolderTheIndexLacks_IsTakenIn()
    {
        var added = await (await LoadedAsync()).AddAsync(Record("dl_1"), Token);
        await File.WriteAllTextAsync(IndexPath, """{ "downloads": [] }""", Token);

        var store = await LoadedAsync();

        Assert.Equal(added.Folder, Assert.Single(store.All).Folder);
        Assert.Single((await LoadedAsync()).All);
        Assert.Contains(added.Folder.Replace("\\", "\\\\", StringComparison.Ordinal), await File.ReadAllTextAsync(IndexPath, Token),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordThatCannotBeRead_IsADamagedRow_SaidOnce_AndDeletable()
    {
        var added = await (await LoadedAsync()).AddAsync(Record("dl_1"), Token);
        await File.WriteAllTextAsync(Path.Combine(added.Folder, DownloadPaths.Record), "{", Token);

        var store = await LoadedAsync();
        store.RecheckFolders();
        store.RecheckFolders();

        var damaged = Assert.Single(store.All);
        Assert.True(damaged.IsDamaged);
        Assert.True(damaged.FilesMissing);
        Assert.Equal("Chapter Five", damaged.Record.Title);
        Assert.Single(_logs.Lines, line => line.Contains("damaged", StringComparison.Ordinal));

        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token);

        Assert.False(Directory.Exists(added.Folder));
    }

    [Theory]
    [InlineData("scope", null)]
    [InlineData("title", null)]
    [InlineData("download_id", "")]
    [InlineData("media_item_id", null)]
    [InlineData("quality", null)]
    [InlineData("expected_bytes", -1)]
    [InlineData("revision", -1)]
    public async Task ARecordWithAValueItCannotHave_IsDamaged(string member, object? value)
    {
        var added = await (await LoadedAsync()).AddAsync(Record("dl_1"), Token);
        string path = Path.Combine(added.Folder, DownloadPaths.Record);
        var record = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!;
        record[member] = value switch
        {
            string text => JsonValue.Create(text),
            int number => JsonValue.Create(number),
            _ => null,
        };
        await File.WriteAllTextAsync(path, record.ToJsonString(), Token);

        var row = Assert.Single((await LoadedAsync()).All);

        Assert.True(row.IsDamaged);
    }

    [Fact]
    public async Task AValidatorOrMediaFileItCannotUse_IsLeftOut()
    {
        await (await LoadedAsync()).AddAsync(Record("dl_1") with { Validator = "W/\"weak\"", MediaFile = "download.json" }, Token);

        var row = Assert.Single((await LoadedAsync()).All);

        Assert.False(row.IsDamaged);
        Assert.Null(row.Record.Validator);
        Assert.Null(row.Record.MediaFile);
        Assert.Null(row.MediaPath);
    }

    [Fact]
    public async Task ADownloadKeptAgain_TakesBackItsUnsentServerDelete()
    {
        var store = await LoadedAsync();
        await store.AddAsync(Record("dl_1"), Token);
        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token);
        Assert.Single(store.PendingDeletes(Scope7));

        await store.AddAsync(Record("dl_1"), Token);

        Assert.Empty(store.PendingDeletes(Scope7));
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(IndexPath, Token));
        Assert.Equal(0, index.RootElement.GetProperty("pending_deletes").GetArrayLength());
        Assert.Empty((await LoadedAsync()).PendingDeletes(Scope7));
    }

    [Fact]
    public async Task AnArtworkNameTheStoreDoesNotMake_IsLeftOut()
    {
        var files = new Dictionary<string, string>
        {
            ["poster"] = "poster.png",
            ["backdrop"] = "..\\..\\elsewhere.jpg",
            ["series_poster"] = "poster.jpg",
            ["logo"] = "logo.exe",
        };
        await (await LoadedAsync()).AddAsync(Record("dl_1") with { ArtworkFiles = files }, Token);

        var row = Assert.Single((await LoadedAsync()).All);

        Assert.Equal(new Dictionary<string, string> { ["poster"] = "poster.png" }, row.Record.ArtworkFiles);
    }

    [Fact]
    public async Task AFailedIndexWrite_IsWrittenWithTheNextChange()
    {
        var store = await LoadedAsync();
        BlockIndex();

        await store.AddAsync(Record("dl_1"), Token);

        Assert.Contains("index could not be written", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, _logs.All, StringComparison.OrdinalIgnoreCase);
        UnblockIndex();

        await store.UpdateAsync(Scope7, "dl_1", r => r with { State = DownloadState.Queued }, Token);

        Assert.True(File.Exists(IndexPath));
        Assert.Contains("\"dl_1\"", await File.ReadAllTextAsync(IndexPath, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemoveWhoseIndexCannotBeWritten_KeepsTheFolderUntilItIs()
    {
        var store = await LoadedAsync();
        var added = await store.AddAsync(Record("dl_1"), Token);
        BlockIndex();

        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token);

        Assert.Empty(store.All);
        Assert.True(Directory.Exists(added.Folder));
        UnblockIndex();

        store.RecheckFolders();
        await store.FolderWork;

        Assert.False(Directory.Exists(added.Folder));
        var again = await LoadedAsync();
        Assert.Empty(again.All);
        Assert.Equal([new PendingDelete(Scope7, "dl_1")], again.PendingDeletes(Scope7));
    }

    [Fact]
    public async Task AFolderThatCannotBeDeletedYet_IsDeletedLater()
    {
        var store = await LoadedAsync();
        var added = await store.AddAsync(Record("dl_1"), Token);
        var held = new FileStream(added.PartPath, FileMode.Create, FileAccess.Write, FileShare.None);

        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: false, Token);

        Assert.True(Directory.Exists(added.Folder));
        Assert.Contains("folder_deletes", await File.ReadAllTextAsync(IndexPath, Token), StringComparison.Ordinal);
        await held.DisposeAsync();

        await LoadedAsync();

        Assert.False(Directory.Exists(added.Folder));
        Assert.Empty((await LoadedAsync()).All);
    }

    [Fact]
    public async Task ADownloadAddedAgain_KeepsItsFolderFromAnEarlierRemove()
    {
        var store = await LoadedAsync();
        await store.AddAsync(Record("dl_1"), Token);
        BlockIndex();
        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: false, Token);
        UnblockIndex();

        var again = await store.AddAsync(Record("dl_1") with { Revision = 2 }, Token);
        store.RecheckFolders();
        await store.FolderWork;

        Assert.True(File.Exists(Path.Combine(again.Folder, DownloadPaths.Record)));
        Assert.Equal(2, Assert.Single((await LoadedAsync()).All).Record.Revision);
    }

    [Fact]
    public async Task AFailedLoad_IsTriedAgain()
    {
        await (await LoadedAsync()).AddAsync(Record("dl_1"), Token);
        await File.WriteAllTextAsync(IndexPath, "{", Token);
        var store = new DownloadStore(IndexPath, _preferences, new FailingOnceClock(), _logs.For<DownloadStore>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(Token));
        await store.LoadAsync(Token);

        Assert.Single(store.All);
    }

    [Fact]
    public async Task TheStartScan_WalksOnlyGranarysLayout()
    {
        var added = await (await LoadedAsync()).AddAsync(Record("dl_1"), Token);
        // A downloads folder that is also someone's media library.
        for (int i = 0; i < 200; i++)
        {
            Directory.CreateDirectory(Path.Combine(Root, $"Movie {i}", "Extras", "acct-1", "7", "dl_9"));
        }
        for (int i = 0; i < 10; i++)
        {
            Directory.CreateDirectory(Path.Combine(Root, $"music{i}", "rock", "7", "dl_9"));
        }
        await File.WriteAllTextAsync(IndexPath, """{ "downloads": [] }""", Token);

        var store = await LoadedAsync();

        Assert.Equal(added.Folder, Assert.Single(store.All).Folder);
        // The root, srv1, acct-1, 7 and the ten lower-case folders a server could be named; nothing below them.
        Assert.Equal(new DownloadStore.ScanCount(14, 1, false), store.LastScan);
    }

    [Fact]
    public async Task TheStartScan_DoesNotReadWhatTheIndexLoaded()
    {
        await (await LoadedAsync()).AddAsync(Record("dl_1"), Token);

        var store = await LoadedAsync();

        Assert.Single(store.All);
        Assert.Equal(0, store.LastScan.Records);
    }

    [Fact]
    public async Task TheStartScan_StopsAtItsLimit_AndSaysSo()
    {
        await (await LoadedAsync()).AddAsync(Record("dl_1"), Token);
        await File.WriteAllTextAsync(IndexPath, """{ "downloads": [] }""", Token);
        var store = new DownloadStore(IndexPath, _preferences, _time, _logs.For<DownloadStore>()) { ScanLimit = 2 };

        await store.LoadAsync(Token);

        Assert.Empty(store.All);
        Assert.True(store.LastScan.Capped);
        Assert.Contains("stopped after 2 folders", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderDeleteUnderAnotherRoot_StaysWhenTheIdIsAddedAgain()
    {
        var store = await LoadedAsync();
        var old = await store.AddAsync(Record("dl_1"), Token);
        var held = new FileStream(old.PartPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: false, Token);
        await _preferences.SetFolderAsync(Path.Combine(_directory, "other-drive"));

        var again = await store.AddAsync(Record("dl_1"), Token);
        await held.DisposeAsync();
        store.RecheckFolders();
        await store.FolderWork;

        Assert.NotEqual(old.Folder, again.Folder);
        Assert.False(Directory.Exists(old.Folder));
        Assert.True(File.Exists(Path.Combine(again.Folder, DownloadPaths.Record)));
        Assert.Single((await LoadedAsync()).All);
    }

    [Fact]
    public async Task TheDeletePass_TakesOnlyTheFoldersOfTheIndexItWrote()
    {
        var store = await LoadedAsync();
        await store.AddAsync(Record("dl_1"), Token);
        var second = await store.AddAsync(Record("dl_2"), Token);
        Task? removeSecond = null;
        bool? secondThereAfterFirstPass = null;
        store.IndexWritten = () =>
        {
            store.IndexWritten = null;
            // A remove that lands after the first pass wrote its index: its folder is not that pass's to delete.
            removeSecond = store.RemoveAsync(Scope7, "dl_2", queueServerDelete: false, Token);
        };
        store.FoldersDeleted = () =>
        {
            store.FoldersDeleted = null;
            secondThereAfterFirstPass = Directory.Exists(second.Folder);
        };

        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: false, Token);
        await removeSecond!;

        Assert.True(secondThereAfterFirstPass);
        Assert.False(Directory.Exists(second.Folder));
        Assert.Empty((await LoadedAsync()).All);
    }

    [Fact]
    public async Task AChangedHandlerThatThrows_DoesNotSkipTheWrite()
    {
        var store = await LoadedAsync();
        var added = await store.AddAsync(Record("dl_1"), Token);
        store.Changed += (_, _) => throw new InvalidOperationException("A handler failed.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(Scope7, "dl_1", queueServerDelete: true, Token));

        Assert.False(Directory.Exists(added.Folder));
        var again = await LoadedAsync();
        Assert.Empty(again.All);
        Assert.Single(again.PendingDeletes(Scope7));
    }

    [Fact]
    public async Task AnIdAddedAgain_HasItsFolderDeleteGoneFromTheIndex_BeforeItsFolderIsMade()
    {
        var store = await LoadedAsync();
        var old = await store.AddAsync(Record("dl_1"), Token);
        var held = new FileStream(old.PartPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await store.RemoveAsync(Scope7, "dl_1", queueServerDelete: false, Token);
        await held.DisposeAsync();
        Assert.Contains("folder_deletes", await File.ReadAllTextAsync(IndexPath, Token), StringComparison.Ordinal);
        string? indexWhenMade = null;
        store.FolderMade = _ => indexWhenMade = File.ReadAllText(IndexPath);

        await store.AddAsync(Record("dl_1"), Token);

        // A crash right after the folder was made could not delete it at the next start.
        Assert.NotNull(indexWhenMade);
        Assert.DoesNotContain(old.Folder.Replace("\\", "\\\\", StringComparison.Ordinal), indexWhenMade, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(old.Folder));
    }

    [Fact]
    public async Task AFolderWhoseRecordCannotBeWritten_IsNotLeftBehind()
    {
        var store = await LoadedAsync();
        string? folder = null;
        // A folder where the record belongs: the record cannot be put there.
        store.FolderMade = made => Directory.CreateDirectory(Path.Combine(folder = made, DownloadPaths.Record));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.AddAsync(Record("dl_1"), Token));

        Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
        Assert.NotNull(folder);
        Assert.False(Directory.Exists(folder));
        Assert.Empty(store.All);
    }

    // A directory where the index belongs: every write of it fails until it goes.
    private void BlockIndex()
    {
        if (File.Exists(IndexPath))
        {
            File.Delete(IndexPath);
        }
        Directory.CreateDirectory(IndexPath);
    }

    private void UnblockIndex() => Directory.Delete(IndexPath);

    private static JsonNode Entry(JsonNode valid, Action<JsonObject> change)
    {
        var entry = valid.DeepClone().AsObject();
        change(entry);
        return entry;
    }

    // Its first reading of the time fails, as a load that throws once.
    private sealed class FailingOnceClock : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _reads) == 1 ? throw new InvalidOperationException("The clock failed.") : ManualTimeProvider.DefaultStart;
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a\\b")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT1")]
    [InlineData("x.")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a:b")]
    [InlineData("an-id-that-goes-on-and-on-well-past-forty-characters-and-more-still")]
    public void Segment_KeepsEveryIdInOneFolder(string value)
    {
        string segment = DownloadPaths.Segment(value);

        Assert.DoesNotContain(segment, c => c is '/' or '\\' or ':');
        Assert.NotEqual(".", segment);
        Assert.NotEqual("..", segment);
        Assert.False(segment.EndsWith('.'));
        Assert.DoesNotContain(segment.Split('.')[0].ToUpperInvariant(), new[] { "CON", "PRN", "AUX", "NUL", "COM1", "LPT1" });
        Assert.InRange(segment.Length, 1, 64);
        Assert.All(segment, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
    }

    [Fact]
    public void Segment_KeepsAPlainIdAsItIs_AndTellsLookalikesApart()
    {
        Assert.Equal("dl_01h-x.y", DownloadPaths.Segment("dl_01h-x.y"));
        Assert.NotEqual(DownloadPaths.Segment("a/b"), DownloadPaths.Segment("a_b"));
        Assert.Equal(DownloadPaths.Segment("a/b"), DownloadPaths.Segment("a/b"));
        Assert.Matches("^a_b-[0-9a-f]{16}$", DownloadPaths.Segment("a/b"));
    }

    [Fact]
    public void Segment_TellsIdsThatDifferOnlyInCaseApart()
    {
        // Windows' folder names ignore case: an id with capitals gets its hash, so "Dl_1" never shares "dl_1"'s folder.
        string[] names = [DownloadPaths.Segment("dl_1"), DownloadPaths.Segment("Dl_1"), DownloadPaths.Segment("DL_1")];

        Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("dl_1", names[0]);
        Assert.Matches("^Dl_1-[0-9a-f]{16}$", names[1]);
    }

    [Fact]
    public void Segment_HashesAPlainIdThatLooksHashed()
    {
        string hashed = DownloadPaths.Segment("a/b");

        // "a_b-<16 hex>" as an id of its own is not left as it is, or it would be "a/b"'s folder.
        Assert.NotEqual(hashed, DownloadPaths.Segment(hashed));
        Assert.Equal("x-0123456789abcde", DownloadPaths.Segment("x-0123456789abcde"));
    }

    [Theory]
    [InlineData("mkv", "media.mkv")]
    [InlineData("MP4", "media.mp4")]
    [InlineData("matroska,webm", "media.mkv")]
    [InlineData("../evil", "media.bin")]
    [InlineData("part", "media.bin")]
    [InlineData(null, "media.bin")]
    public void MediaName_IsAPlainNameInTheFolder(string? container, string expected) =>
        Assert.Equal(expected, DownloadPaths.MediaName(container));

    [Theory]
    [InlineData("media.mkv", true)]
    [InlineData("media.mp4", true)]
    [InlineData("media.part", false)]
    [InlineData("download.json", false)]
    [InlineData("manifest.json", false)]
    [InlineData("MEDIA.MKV", false)]
    [InlineData("media.", false)]
    [InlineData("..\\media.mkv", false)]
    [InlineData("CON", false)]
    [InlineData("media.toolongext", false)]
    public void TheMediaFile_IsOnlyAMediaName(string mediaFile, bool kept)
    {
        var download = new StoredDownload(Record() with { MediaFile = mediaFile }, Path.Combine(Root, "x"), FilesMissing: false);

        Assert.Equal(kept, DownloadPaths.IsMediaName(mediaFile));
        Assert.Equal(kept ? Path.Combine(Root, "x", mediaFile) : null, download.MediaPath);
    }

    [Fact]
    public void ArtworkAndSubtitleNames_TakeTheirKindAndFormat()
    {
        Assert.Equal("poster.jpg", DownloadPaths.ArtworkName("poster", "image/jpeg"));
        Assert.Equal("series_poster.webp", DownloadPaths.ArtworkName("series_poster", "image/webp"));
        Assert.Equal("backdrop.png", DownloadPaths.ArtworkName("backdrop", "image/png"));
        Assert.Equal("logo.img", DownloadPaths.ArtworkName("logo", null));
        Assert.Equal("sub-0.srt", DownloadPaths.SubtitleName(0, "srt"));
        Assert.Equal("sub-3.ass", DownloadPaths.SubtitleName(3, "ASS"));
        Assert.Equal("sub-1.sub", DownloadPaths.SubtitleName(1, "../x"));
    }
}
