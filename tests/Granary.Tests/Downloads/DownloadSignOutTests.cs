using System.Net;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Signing out of a server or removing it takes its downloads along: the viewer is asked first, the server hears the
/// deletes it can be told of, the files go, then the sign-out. A sign-in that expired keeps them.
/// </summary>
public sealed class DownloadSignOutTests : IAsyncDisposable
{
    // With its record beside it, a download of this many bytes reads "1.5 MB".
    private const int OneAndAHalfMegabytes = 1536 * 1024;

    private static readonly Uri LanBase = new("https://lan.example/silo");
    private static readonly DownloadScope OtherServer = new("srv2", "1", "7");

    private readonly DownloadHarness _h = new();
    private readonly FakeConfirmation _confirm = new();
    private readonly InMemoryServerRegistry _registry = new();
    private readonly CredentialStore _credentials;

    public DownloadSignOutTests() => _credentials = new CredentialStore(new InMemorySecretStore(), _h.Logs.For<CredentialStore>());

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private DownloadSignOut NewSignOut() => new(_h.Store, _h.Api, _h.Session, _credentials, _confirm, _h.Time, _h.Logs.For<DownloadSignOut>());

    // A completed download with its file on the disk.
    private async Task<StoredDownload> KeptAsync(string id, DownloadScope? scope = null)
    {
        var stored = await _h.AddAsync(id, DownloadState.Completed, scope: scope, mediaItemId: "mv_" + id);
        await File.WriteAllBytesAsync(stored.PartPath, new byte[OneAndAHalfMegabytes], Token);
        return stored;
    }

    private static SessionCredentials SignedIn(string accountId) =>
        new("session-" + accountId, "access", "refresh", ManualTimeProvider.DefaultStart.AddHours(1), 3600, "7", null,
            new Account(accountId, "elena", "", "user", [], true, false, null));

    // NAS (srv1, the active one, signed in as account 1) and Basement (srv2).
    private async Task<ServersViewModel> ServersAsync()
    {
        await _registry.AddOrUpdateAsync(ScriptedSilo.Base, "NAS", "server-srv1", Token);
        await _registry.AddOrUpdateAsync(LanBase, "Basement", "server-srv2", Token);
        await _registry.SetActiveAsync("srv1", Token);
        await _credentials.SetAsync("srv1", SignedIn("1"), Token);
        return new ServersViewModel(_registry, _h.Session, new RecordingNavigation(), new ImmediateDispatcher(), NewSignOut());
    }

    private static ServerItemViewModel Item(ServersViewModel servers, string id) => servers.Servers.Single(s => s.Id == id);

    private IReadOnlyList<SentRequest> DeletesSent => [.. _h.Silo.Sent.Where(r => r.Method == HttpMethod.Delete)];

    private IEnumerable<string> Kept => _h.Store.All.Select(d => d.Record.DownloadId);

    [Fact]
    public async Task SigningOut_AsksWithTheCountAndSize()
    {
        await KeptAsync("dl_1");
        await KeptAsync("dl_8", Scope8);
        await KeptAsync("dl_x", OtherServer);
        _confirm.Answer = false;
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await _h.Store.RemoveAsync(Scope8, "dl_8", queueServerDelete: false, Token);
        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));

        Assert.Equal(("Sign out of NAS?", "2 downloads (3.0 MB) on this PC are deleted with it.", "Sign out"), _confirm.Asked[0]);
        Assert.Equal("1 download (1.5 MB) on this PC is deleted with it.", _confirm.Asked[1].Message);
    }

    [Fact]
    public async Task Declined_KeepsEverything_AndDoesNotSignOut()
    {
        var kept = await KeptAsync("dl_1");
        _confirm.Answer = false;
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));

        Assert.Single(_confirm.Asked);
        Assert.DoesNotContain("sign-out:srv1", _h.Session.Calls);
        Assert.Equal(["dl_1"], Kept);
        Assert.True(File.Exists(kept.PartPath));
        Assert.Empty(DeletesSent);
        Assert.Equal("", servers.Status);
    }

    [Fact]
    public async Task Confirmed_TakesTheDownloadsOff_TellsTheServer_ThenSignsOut()
    {
        var mine = await KeptAsync("dl_1");
        var otherProfile = await KeptAsync("dl_8", Scope8);
        var otherServer = await KeptAsync("dl_x", OtherServer);
        List<string>? keptAtDelete = null;
        bool signedOutAtDelete = true;
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), () =>
        {
            keptAtDelete = [.. Kept];
            signedOutAtDelete = _h.Session.Calls.Count > 0;
            return ScriptedSilo.NoContent()();
        });
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await _h.Store.FolderWork;

        Assert.Equal("7", Assert.Single(DeletesSent).AsProfile);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")));
        Assert.Equal(["dl_x"], keptAtDelete);
        Assert.False(signedOutAtDelete);
        Assert.False(Directory.Exists(mine.Folder));
        Assert.False(Directory.Exists(otherProfile.Folder));
        Assert.Equal(["dl_x"], Kept);
        Assert.True(File.Exists(otherServer.PartPath));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
        Assert.Equal("Signed out of NAS.", servers.Status);
    }

    [Fact]
    public async Task ADeleteStillToSend_IsSentBeforeSigningOut()
    {
        await KeptAsync("dl_2");
        await _h.Store.RemoveAsync(Scope7, "dl_2", queueServerDelete: true, Token);
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));

        Assert.Empty(_confirm.Asked);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_2")));
        Assert.Empty(_h.Store.PendingDeletes(Scope7));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
    }

    [Fact]
    public async Task NoDownloads_SignsOutWithoutAQuestion()
    {
        var otherServer = await KeptAsync("dl_x", OtherServer);
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));

        Assert.Empty(_confirm.Asked);
        Assert.Empty(DeletesSent);
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
        Assert.True(File.Exists(otherServer.PartPath));
    }

    [Fact]
    public async Task SigningOutOfAServerNotInUse_TakesItsSavedAccountsDownloads()
    {
        var saved = await KeptAsync("dl_a", new DownloadScope("srv2", "2", "7"));
        var expired = await KeptAsync("dl_b", new DownloadScope("srv2", "3", "7"));
        var active = await KeptAsync("dl_1");
        using var servers = await ServersAsync();
        await _credentials.SetAsync("srv2", SignedIn("2"), Token);

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv2"));
        await _h.Store.FolderWork;

        Assert.Equal(("Sign out of Basement?", "1 download (1.5 MB) on this PC is deleted with it.", "Sign out"), Assert.Single(_confirm.Asked));
        // Its profile is not the active one: the server cannot be told.
        Assert.Empty(DeletesSent);
        Assert.False(Directory.Exists(saved.Folder));
        Assert.True(File.Exists(expired.PartPath));
        Assert.True(File.Exists(active.PartPath));
        Assert.Equal(["sign-out:srv2"], _h.Session.Calls);
    }

    [Fact]
    public async Task TheAccount_IsTheSessionsOrTheSavedSignIns()
    {
        var signOut = NewSignOut();
        await _credentials.SetAsync("srv2", SignedIn("2"), Token);

        Assert.Equal("1", await signOut.AccountOfAsync("srv1", Token));
        Assert.Equal("2", await signOut.AccountOfAsync("srv2", Token));
        Assert.Null(await signOut.AccountOfAsync("srv3", Token));
    }

    [Fact]
    public async Task RemovingAServer_DeletesEveryAccountsDownloadsThere()
    {
        var mine = await KeptAsync("dl_1");
        var otherProfile = await KeptAsync("dl_8", Scope8);
        var otherAccount = await KeptAsync("dl_9", new DownloadScope("srv1", "2", "9"));
        var otherServer = await KeptAsync("dl_x", OtherServer);
        bool signedOutAtDelete = true;
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), () =>
        {
            signedOutAtDelete = _h.Session.Calls.Count > 0;
            return ScriptedSilo.NoContent()();
        });
        using var servers = await ServersAsync();

        await servers.RemoveCommand.ExecuteAsync(Item(servers, "srv1"));
        await _h.Store.FolderWork;

        // The question before removing is the app's own.
        Assert.Empty(_confirm.Asked);
        Assert.Equal("7", Assert.Single(DeletesSent).AsProfile);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")));
        Assert.False(signedOutAtDelete);
        Assert.All(new[] { mine, otherProfile, otherAccount }, d => Assert.False(Directory.Exists(d.Folder)));
        Assert.Equal(["dl_x"], Kept);
        Assert.True(File.Exists(otherServer.PartPath));
        Assert.Equal(["sign-out:srv1", "activate:srv2"], _h.Session.Calls);
        Assert.Null(_registry.Find("srv1"));
    }

    [Fact]
    public async Task TheRemoveQuestion_NamesTheDownloads()
    {
        await KeptAsync("dl_1");
        await KeptAsync("dl_8", Scope8);
        await KeptAsync("dl_9", new DownloadScope("srv1", "2", "9"));
        await KeptAsync("dl_x", OtherServer);

        var everyAccount = await NewSignOut().SummarizeAsync("srv1", null, Token);

        Assert.Equal("2 downloads (3.0 GB) on this PC are deleted with it.", DownloadSignOut.Note(new(2, 3L << 30)));
        Assert.Equal("1 download (1.5 GB) on this PC is deleted with it.", DownloadSignOut.Note(new(1, 1536L << 20)));
        Assert.Null(DownloadSignOut.Note(new(0, 0)));
        Assert.Equal("3 downloads (4.5 MB) on this PC are deleted with it.", DownloadSignOut.Note(everyAccount));
    }

    [Fact]
    public async Task AnExpiredSignIn_KeepsTheDownloads()
    {
        var kept = await KeptAsync("dl_1");
        await _credentials.SetAsync("srv1", SignedIn("1"), Token);
        _ = NewSignOut();

        // As the authentication handler does when the server no longer takes the session.
        await _credentials.RemoveAsync("srv1", "session-1", Token);
        _h.SignOut();
        await _h.Store.FolderWork;

        Assert.Equal(["dl_1"], Kept);
        Assert.True(File.Exists(kept.PartPath));
        Assert.Empty(DeletesSent);
        Assert.Empty(_confirm.Asked);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswer_StillLosesItsLocalFiles()
    {
        var kept = await KeptAsync("dl_1");
        _h.Silo.OnCancellable(HttpMethod.Delete, EntryPath("dl_1"), async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var servers = await ServersAsync();

        var signingOut = servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await Until(() => DeletesSent.Count == 1, "the delete went out");
        int keptWhileWaiting = Kept.Count();
        int callsWhileWaiting = _h.Session.Calls.Count;
        _h.Time.Advance(DownloadSignOut.DeleteBudget);
        await signingOut.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await _h.Store.FolderWork;

        Assert.Equal(0, keptWhileWaiting);
        Assert.Equal(0, callsWhileWaiting);
        Assert.False(Directory.Exists(kept.Folder));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
        await _h.RestartAsync();
        Assert.Empty(_h.Store.All);
        Assert.Empty(_h.Store.PendingDeletes(Scope7));
    }

    [Fact]
    public async Task AServerThatCannotBeReached_StillLosesItsLocalFiles()
    {
        var first = await KeptAsync("dl_1");
        var second = await KeptAsync("dl_2");
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), ScriptedSilo.NetworkError());
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_2"), ScriptedSilo.NetworkError());
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await _h.Store.FolderWork;

        // The first one that cannot go says the server cannot be reached now; the rest are not tried.
        Assert.Single(DeletesSent);
        Assert.False(Directory.Exists(first.Folder));
        Assert.False(Directory.Exists(second.Folder));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
        await _h.RestartAsync();
        Assert.Empty(_h.Store.PendingDeletes(Scope7));
        Assert.DoesNotContain("nas.example", _h.Logs.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEntryTheServerRefuses_DoesNotStopTheOthers()
    {
        await KeptAsync("dl_1");
        await KeptAsync("dl_2");
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), ScriptedSilo.Problem(403, "permission_denied"));
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));

        Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_2")));
        Assert.Empty(_h.Store.All);
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
    }

    [Theory]
    [InlineData(401, "session_expired")]
    [InlineData(429, "rate_limited")]
    public async Task ARefusedSignInOrABusyServer_StopsTheDeletesAtTheFirst(int status, string code)
    {
        await KeptAsync("dl_1");
        await KeptAsync("dl_2");
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), ScriptedSilo.Problem(status, code));
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));

        Assert.Single(DeletesSent);
        Assert.Empty(_h.Store.All);
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
    }

    [Fact]
    public async Task TheProfileMenusSignOut_AsksAndTakesTheDownloadsAlong()
    {
        var kept = await KeptAsync("dl_1");
        var http = _h.Silo.Http;
        using var sidebar = new SidebarViewModel(_h.Session, new ProfilesApi(http),
            new VisibleLibraries(new CatalogApi(http), new SettingsApi(http), _h.Logs.For<VisibleLibraries>()), new RecordingNavigation(),
            new ImmediateDispatcher(), _h.Time, _h.Logs.For<SidebarViewModel>(), downloads: NewSignOut());

        await sidebar.SignOutCommand.ExecuteAsync(null);
        await _h.Store.FolderWork;

        Assert.Equal("Sign out of NAS?", Assert.Single(_confirm.Asked).Title);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")));
        Assert.False(Directory.Exists(kept.Folder));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
    }

    [Fact]
    public async Task ChangePasswordsSignOut_AsksAndTakesTheDownloadsAlong()
    {
        var kept = await KeptAsync("dl_1");
        // A temporary password: signed in, no profile acting yet, so the server cannot be told of the deletes.
        _h.Session.State = SessionState.PasswordChangeRequired;
        _h.Session.ProfileId = null;
        var viewModel = new ChangePasswordViewModel(new AccountApi(_h.Silo.Http), _h.Session, new RecordingLogger<ChangePasswordViewModel>(),
            NewSignOut());

        await viewModel.SignOutCommand.ExecuteAsync(null);
        await _h.Store.FolderWork;

        Assert.Equal("Sign out of NAS?", Assert.Single(_confirm.Asked).Title);
        Assert.Empty(DeletesSent);
        Assert.False(Directory.Exists(kept.Folder));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
        Assert.Equal("", viewModel.Status);
    }

    [Fact]
    public async Task ChangePasswordsSignOut_Declined_SignsNothingOut()
    {
        var kept = await KeptAsync("dl_1");
        _confirm.Answer = false;
        var viewModel = new ChangePasswordViewModel(new AccountApi(_h.Silo.Http), _h.Session, new RecordingLogger<ChangePasswordViewModel>(),
            NewSignOut());

        await viewModel.SignOutCommand.ExecuteAsync(null);

        Assert.Empty(_h.Session.Calls);
        Assert.True(File.Exists(kept.PartPath));
    }

    [Fact]
    public async Task ATransferUnderWay_StopsBeforeTheServerHearsTheDeletes()
    {
        var stored = await _h.AddAsync("dl_1");
        var stream = _h.ServeGated("dl_1", DownloadPayloads.Source(3000));
        var queue = _h.StartQueue();
        stream.Release(1000);
        await Until(() => stream.Delivered == 1000, "the transfer is under way");
        int rowsAtDelete = -1;
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), () =>
        {
            rowsAtDelete = _h.Store.All.Count;
            return ScriptedSilo.NoContent()();
        });
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await SettleAsync(queue);
        stream.ReleaseAll();
        await SettleAsync(queue);
        await _h.Store.FolderWork;

        Assert.Equal(0, rowsAtDelete);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, EntryPath("dl_1")));
        Assert.Single(_h.FileRequests("dl_1"));
        Assert.Equal(1000, stream.Delivered);
        Assert.False(Directory.Exists(stored.Folder));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
    }

    [Fact]
    public async Task ACreateThatAnswersAfterTheSignOut_KeepsNothing()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Post, ListPath, async () =>
        {
            await answer.Task;
            return await ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready")))();
        });
        var manager = _h.NewManager();
        using var servers = await ServersAsync();

        var starting = manager.StartAsync(new DownloadRequest("mv_1", null, null, "original", "Example Movie", null, null, null), Token);
        await Until(() => _h.Silo.SentTo(HttpMethod.Post, ListPath).Count == 1, "the create went");
        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        answer.SetResult();

        Assert.Equal(DownloadStartResult.Failed, await starting);
        Assert.Empty(_h.Store.All);
        Assert.False(Directory.Exists(DownloadPaths.Folder(_h.Root, Scope7, "dl_1")));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
    }

    [Fact]
    public async Task AShareThatDoesNotAnswer_GetsTheQuestionWithoutTheSize()
    {
        await KeptAsync("dl_1");
        using var share = new StalledDisk();
        _h.Store.MeasuringFolder = _ => share.Hold();
        _confirm.Answer = false;
        using var servers = await ServersAsync();

        var signingOut = servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await share.Reached.WaitAsync(TimeSpan.FromSeconds(5), Token);
        _h.Time.Advance(DownloadSignOut.SummaryBudget);
        await signingOut.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal(("Sign out of NAS?", "1 download on this PC is deleted with it.", "Sign out"), Assert.Single(_confirm.Asked));
        Assert.Contains("did not come within 5 s", _h.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadsThatCannotBeReadInTime_StillGetTheQuestion_AndGoOnceTheyAreRead()
    {
        var kept = await KeptAsync("dl_1");
        using var share = new StalledDisk();
        var store = new DownloadStore(_h.IndexPath, _h.Preferences, _h.Time, _h.Logs.For<DownloadStore>()) { Loading = share.Hold };
        var signOut = new DownloadSignOut(store, _h.Api, _h.Session, _credentials, _confirm, _h.Time, _h.Logs.For<DownloadSignOut>());

        var summarizing = signOut.SummarizeAsync("srv1", "1", Token);
        await share.Reached.WaitAsync(TimeSpan.FromSeconds(5), Token);
        _h.Time.Advance(DownloadSignOut.SummaryBudget);
        var summary = await summarizing.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var clearing = signOut.ClearAsync("srv1", "1", Token);
        await _h.Time.WaitUntilAsync(() => _h.Time.NextDue == DownloadSignOut.StoreBudget, Token);
        _h.Time.Advance(DownloadSignOut.StoreBudget);
        await clearing.WaitAsync(TimeSpan.FromSeconds(5), Token);
        share.Release();
        await Until(() => !Directory.Exists(kept.Folder), "its folder went once the downloads were read");
        await store.FolderWork;

        Assert.Equal("Its downloads on this PC are deleted with it.", DownloadSignOut.Note(summary));
        Assert.Empty(store.All);
        Assert.Empty(DeletesSent);
    }

    [Fact]
    public async Task AnIndexThatCannotBeWrittenInTime_DoesNotHoldTheSignOutUp()
    {
        var kept = await KeptAsync("dl_1");
        using var disk = new StalledDisk();
        _h.Store.WritingIndex = disk.Hold;
        using var servers = await ServersAsync();

        var signingOut = servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await disk.Reached.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await _h.Time.WaitUntilAsync(() => _h.Time.NextDue == DownloadSignOut.StoreBudget, Token);
        _h.Time.Advance(DownloadSignOut.StoreBudget);
        await signingOut.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var calls = _h.Session.Calls.ToList();
        var keptAtSignOut = Kept.ToList();
        _h.Store.WritingIndex = null;
        disk.Release();
        await Until(() => !Directory.Exists(kept.Folder), "its folder went once the index was written");

        Assert.Equal(["sign-out:srv1"], calls);
        Assert.Empty(keptAtSignOut);
        Assert.Contains("was not written within 5 s", _h.Logs.All, StringComparison.Ordinal);
        await _h.RestartAsync();
        Assert.Empty(_h.Store.All);
    }

    [Fact]
    public async Task TheFoldersGoAfterTheSignOut_SoAShareThatDoesNotAnswerHoldsNothingUp()
    {
        var kept = await KeptAsync("dl_1");
        using var share = new StalledDisk();
        _h.Store.IndexWritten = share.Hold;
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1")).WaitAsync(TimeSpan.FromSeconds(5), Token);
        bool folderAtSignOut = Directory.Exists(kept.Folder);
        _h.Store.IndexWritten = null;
        share.Release();
        await _h.Store.FolderWork;

        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
        Assert.True(folderAtSignOut);
        Assert.False(Directory.Exists(kept.Folder));
    }

    /// <summary>
    /// A drive or a share that does not answer: what reaches it waits until the test lets it go (at most half a minute, so a
    /// failed test leaves no thread behind for long).
    /// </summary>
    private sealed class StalledDisk : IDisposable
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _released = new();

        public Task Reached => _reached.Task;

        public void Hold()
        {
            _reached.TrySetResult();
            _released.Wait(TimeSpan.FromSeconds(30));
        }

        public void Release() => _released.Set();

        // The event stays: a thread may still be waking from it.
        public void Dispose() => _released.Set();
    }
}
