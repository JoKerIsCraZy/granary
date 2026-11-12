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

    // A title none of the tests keeps before it is asked for.
    private static readonly DownloadRequest NextEpisode = new("ep_5", "sr_1", null, "original", "Chapter Five", "Example Series", 1, 5);

    private readonly DownloadHarness _h = new();
    private readonly FakeConfirmation _confirm = new();
    private readonly InMemoryServerRegistry _registry = new();
    private readonly CredentialStore _credentials;

    private readonly OfflineProgressQueue _progress;

    public DownloadSignOutTests()
    {
        _credentials = new CredentialStore(new InMemorySecretStore(), _h.Logs.For<CredentialStore>());
        _progress = new OfflineProgressQueue(Path.Combine(_h.Directory, "data", "offline-progress.json"), _h.Time, _h.Logs.For<OfflineProgressQueue>());
        _settings = new(() => new Granary.Core.Playback.KeptPlaybackSettings(Path.Combine(_h.Directory, "data", "playback-settings.json"), _h.Session,
            _h.Logs.For<Granary.Core.Playback.KeptPlaybackSettings>()));
    }

    public async ValueTask DisposeAsync()
    {
        await _progress.LatestSave;
        if (_settings.IsValueCreated)
        {
            await _settings.Value.LatestSave;
        }
        await _h.DisposeAsync();
    }

    private readonly Lazy<Granary.Core.Playback.KeptPlaybackSettings> _settings;

    private DownloadSignOut NewSignOut() =>
        new(_h.Store, _h.Api, _h.Session, _credentials, _confirm, _h.Time, _h.Logs.For<DownloadSignOut>(), _progress, _settings.Value);

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

    // The question was about this account's downloads: another account signed in while it was open gets nothing done to it.
    [Fact]
    public async Task AnotherAccountSignedInWhileAsking_KeepsEverything_AndDoesNotSignOut()
    {
        var kept = await KeptAsync("dl_1");
        _confirm.WhileAsking = () =>
        {
            _h.Session.Account = new Account("2", "elena2", "", "user", [], true, false, null);
            return Task.CompletedTask;
        };
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));

        Assert.Single(_confirm.Asked);
        Assert.DoesNotContain("sign-out:srv1", _h.Session.Calls);
        Assert.Equal(["dl_1"], Kept);
        Assert.True(File.Exists(kept.PartPath));
        Assert.Empty(DeletesSent);
    }

    // The downloads asked about go, but another account signed in while they went stays signed in.
    [Fact]
    public async Task AnotherAccountSignedInWhileTheDownloadsGo_StaysSignedIn()
    {
        await KeptAsync("dl_1");
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), () =>
        {
            _h.Session.Account = new Account("2", "elena2", "", "user", [], true, false, null);
            return ScriptedSilo.NoContent()();
        });
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await _h.Store.FolderWork;

        Assert.Single(DeletesSent);
        Assert.Empty(Kept);
        Assert.DoesNotContain("sign-out:srv1", _h.Session.Calls);
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
    public async Task SigningOut_TakesTheAccountsUnsentPositionsAlong_AndLeavesOtherAccountsAndServersAlone()
    {
        var otherAccount = new DownloadScope("srv1", "2", "7");
        await KeptAsync("dl_1");
        _progress.Record(Scope7, "mv_dl_1", 60_000, 0);
        _progress.Record(Scope8, "ep_5", 60_000, 0);
        _progress.Record(otherAccount, "ep_5", 60_000, 0);
        _progress.Record(OtherServer, "ep_5", 60_000, 0);
        _settings.Value.Keep("srv1/1/7", ["a"], new Granary.Core.Api.Settings.EffectiveSettings([], 1));
        _settings.Value.Keep("srv1/2/7", ["a"], new Granary.Core.Api.Settings.EffectiveSettings([], 1));
        using var servers = await ServersAsync();

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await _progress.LatestSave;

        // The settings kept for its plays without the server go too.
        Assert.Null(_settings.Value.Find("srv1/1/7", ["a"]));
        Assert.NotNull(_settings.Value.Find("srv1/2/7", ["a"]));
        // What the account watched here and had not sent yet is not kept behind its back, on any of its profiles.
        Assert.Empty(_progress.Entries(Scope7));
        Assert.Empty(_progress.Entries(Scope8));
        Assert.Single(_progress.Entries(otherAccount));
        Assert.Single(_progress.Entries(OtherServer));
        Assert.DoesNotContain("mv_dl_1", await File.ReadAllTextAsync(Path.Combine(_h.Directory, "data", "offline-progress.json"), Token),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingAServer_TakesEveryAccountsUnsentPositionsThere()
    {
        var otherAccount = new DownloadScope("srv1", "2", "7");
        _progress.Record(Scope7, "ep_5", 60_000, 0);
        _progress.Record(otherAccount, "ep_5", 60_000, 0);
        _progress.Record(OtherServer, "ep_5", 60_000, 0);
        using var servers = await ServersAsync();

        await servers.RemoveCommand.ExecuteAsync(Item(servers, "srv1"));

        Assert.Empty(_progress.Entries(Scope7));
        Assert.Empty(_progress.Entries(otherAccount));
        Assert.Single(_progress.Entries(OtherServer));
    }

    [Fact]
    public async Task AnExpiredSignIn_KeepsTheUnsentPositions()
    {
        _progress.Record(Scope7, "ep_5", 60_000, 0);
        await _credentials.SetAsync("srv1", SignedIn("1"), Token);
        _ = NewSignOut();

        await _credentials.RemoveAsync("srv1", "session-1", Token);
        _h.SignOut();

        // They go once the account is signed in again.
        Assert.Single(_progress.Entries(Scope7));
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
    public async Task SigningOutWhereTheAccountIsNotKnown_KeepsEveryAccountsDownloadsThere()
    {
        var expired = await KeptAsync("dl_b", new DownloadScope("srv2", "3", "7"));
        using var servers = await ServersAsync();
        await _credentials.SetAsync("srv2", SignedIn("2") with { Account = null }, Token);

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv2"));
        await _h.Store.FolderWork;

        Assert.Empty(_confirm.Asked);
        Assert.True(File.Exists(expired.PartPath));
        Assert.Equal(["dl_b"], Kept);
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
    public async Task ACreateStartedWhileSigningOut_IsRefused_WithoutAskingTheServer()
    {
        await KeptAsync("dl_1");
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Delete, EntryPath("dl_1"), async () =>
        {
            await deleted.Task;
            return await ScriptedSilo.NoContent()();
        });
        _h.Silo.On(HttpMethod.Post, ListPath, ScriptedSilo.Json(HttpStatusCode.Accepted,
            DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_2", "ready", episodeId: "ep_5"))));
        var manager = _h.NewManager();
        using var servers = await ServersAsync();

        var signingOut = servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        await Until(() => DeletesSent.Count == 1, "the sign-out tells the server");
        var started = await manager.StartAsync(NextEpisode, Token);
        deleted.SetResult();
        await signingOut;

        Assert.Equal(DownloadStartResult.Unavailable, started);
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Post, ListPath));
        Assert.Empty(_h.Store.All);
        Assert.False(Directory.Exists(DownloadPaths.Folder(_h.Root, Scope7, "dl_2")));
        Assert.Equal(["sign-out:srv1"], _h.Session.Calls);
    }

    [Fact]
    public async Task SigningInAgainQuickly_KeepsTheNewCreate_AndNotTheOneFromBefore()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Post, ListPath,
            async () =>
            {
                await answer.Task;
                return await ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_1", "ready")))();
            },
            ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_2", "ready", episodeId: "ep_5"))));
        var manager = _h.NewManager();
        using var servers = await ServersAsync();

        var before = manager.StartAsync(new DownloadRequest("mv_1", null, null, "original", "Example Movie", null, null, null), Token);
        await Until(() => _h.Silo.SentTo(HttpMethod.Post, ListPath).Count == 1, "the first create went");
        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        // The same account signs in again on the same profile.
        _h.SignOut();
        _h.Session.State = SessionState.Ready;
        _h.Session.ProfileId = "7";
        _h.Session.RaiseChanged();
        var after = await manager.StartAsync(NextEpisode, Token);
        answer.SetResult();

        Assert.Equal(DownloadStartResult.Started, after);
        Assert.Equal(DownloadStartResult.Failed, await before);
        Assert.Equal(["dl_2"], Kept);
        Assert.False(Directory.Exists(DownloadPaths.Folder(_h.Root, Scope7, "dl_1")));
    }

    [Fact]
    public async Task AFailedSignOut_LetsTheAccountsNewDownloadsRun()
    {
        await KeptAsync("dl_1");
        var queue = _h.StartQueue();
        await SettleAsync(queue);
        var manager = _h.NewManager(queue);
        using var servers = await ServersAsync();
        _h.Session.FailWith = new IOException("The sign-in data could not be saved.");

        await servers.SignOutCommand.ExecuteAsync(Item(servers, "srv1"));
        _h.Session.FailWith = null;
        _h.Silo.On(HttpMethod.Post, ListPath, ScriptedSilo.Json(HttpStatusCode.Accepted,
            DownloadPayloads.Page(null, DownloadPayloads.Entry("dl_2", "ready", episodeId: "ep_5"))));
        _h.ServeAssets("dl_2");
        _h.ServeFile("dl_2", DownloadPayloads.Source(3000));
        var started = await manager.StartAsync(NextEpisode, Token);
        await Until(() => _h.Find("dl_2")?.Record.State == DownloadState.Completed, "the new download ran");

        Assert.Equal("Granary could not save its settings on this computer. Try again.", servers.Status);
        Assert.Equal(DownloadStartResult.Started, started);
        Assert.Equal(["dl_2"], Kept);
    }

    [Fact]
    public async Task AClearThatFailsWhileTellingTheServer_StillLogsWhatTheRemovalHit()
    {
        await KeptAsync("dl_1");
        _h.Silo.OnCancellable(HttpMethod.Delete, EntryPath("dl_1"), async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        _h.Store.Changed += (_, _) => throw new InvalidOperationException("A follower broke.");
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var clearing = NewSignOut().ClearAsync("srv1", "1", giveUp.Token);
        await Until(() => DeletesSent.Count == 1, "the delete went out");
        await giveUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clearing);
        await Until(() => _h.Logs.All.Contains("Taking the downloads off this PC failed in part (InvalidOperationException", StringComparison.Ordinal),
            "the removal's failure was logged");
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
        (await clearing.WaitAsync(TimeSpan.FromSeconds(5), Token)).Dispose();
        share.Release();
        await Until(() => !Directory.Exists(kept.Folder), "its folder went once the downloads were read");
        await store.FolderWork;

        Assert.Equal("Any downloads on this PC are deleted with it.", DownloadSignOut.Note(summary));
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
