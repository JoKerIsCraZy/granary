using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The profile's sidebar: entries hidden and moved, kept on this PC for each server and profile.</summary>
public sealed class SidebarLayoutTests : IDisposable
{
    private static readonly ServerEntry Server = new("srv1", new Uri("https://silo.test"), "JoKeR", null, default, default, null);
    private static readonly string[] Discover = ["ForYou", "Requests", "Calendar", "Notifications"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSessionManager _session = new();
    private readonly LogSink _logs = new();

    public SidebarLayoutTests()
    {
        _session.State = SessionState.Ready;
        _session.ActiveServer = Server;
        _session.ProfileId = "7";
    }

    private string FilePath => Path.Combine(_directory, "sidebar-layout.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SidebarLayout Create() => new(FilePath, _session, new ImmediateDispatcher(), _logs.For<SidebarLayout>());

    [Fact]
    public void AtFirst_EverythingShows_AsItComes()
    {
        var layout = Create();

        Assert.False(layout.IsHidden("List:history"));
        Assert.Equal(Discover, layout.Arrange(SidebarSection.Discover, Discover));
        Assert.False(layout.IsCustomized);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task AHiddenEntry_AndAMovedOne_StaySoAtTheNextStart()
    {
        var layout = Create();

        await layout.SetHiddenAsync("List:history", true);
        await layout.MoveAsync(SidebarSection.Discover, Discover, "Calendar", -1);
        var again = Create();

        Assert.True(again.IsHidden("List:history"));
        Assert.Equal(["ForYou", "Calendar", "Requests", "Notifications"], again.Arrange(SidebarSection.Discover, Discover));
        Assert.True(again.IsCustomized);
    }

    [Fact]
    public async Task MovingTheFirstUp_OrTheLastDown_ChangesNothing()
    {
        var layout = Create();

        await layout.MoveAsync(SidebarSection.Discover, Discover, "ForYou", -1);
        await layout.MoveAsync(SidebarSection.Discover, Discover, "Notifications", 1);

        Assert.False(layout.IsCustomized);
        Assert.Equal(0, layout.Revision);
    }

    [Fact]
    public async Task AnEntryNotPlacedYet_ComesAfterThePlacedOnes()
    {
        var layout = Create();
        await layout.MoveAsync(SidebarSection.Libraries, ["Library:1", "Library:2"], "Library:2", -1);

        var arranged = layout.Arrange(SidebarSection.Libraries, ["Library:1", "Library:2", "Library:3"]);

        Assert.Equal(["Library:2", "Library:1", "Library:3"], arranged);
    }

    [Fact]
    public async Task MovingDownAndUpAgain_IsNoChoiceOfTheProfiles()
    {
        var layout = Create();

        await layout.MoveAsync(SidebarSection.Discover, Discover, "Requests", 1);
        await layout.MoveAsync(SidebarSection.Discover, Discover, "Requests", -1);

        Assert.False(layout.IsCustomized);
        Assert.Equal(Discover, layout.Arrange(SidebarSection.Discover, Discover));
    }

    [Fact]
    public void ALibraryListedTwice_ShowsOnce()
    {
        var layout = Create();

        Assert.Equal(["Library:1", "Library:2"], layout.Arrange(SidebarSection.Libraries, ["Library:1", "Library:2", "Library:1"]));
    }

    [Fact]
    public async Task EachProfile_HasItsOwnSidebar()
    {
        var layout = Create();
        await layout.SetHiddenAsync("Collections", true);
        int before = layout.Revision;

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.False(layout.IsHidden("Collections"));
        Assert.True(layout.Revision > before);
        _session.ProfileId = "7";
        _session.RaiseChanged();
        Assert.True(layout.IsHidden("Collections"));
    }

    [Fact]
    public async Task Reset_ShowsEverythingAgain_AsItComes()
    {
        var layout = Create();
        await layout.SetHiddenAsync("ForYou", true);
        await layout.MoveAsync(SidebarSection.Discover, Discover, "Calendar", -1);

        await layout.ResetAsync();

        Assert.False(layout.IsHidden("ForYou"));
        Assert.Equal(Discover, Create().Arrange(SidebarSection.Discover, Discover));
        Assert.False(layout.IsCustomized);
    }

    [Fact]
    public void AnUnreadableFile_ShowsTheSidebarAsItComes()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        var layout = Create();

        Assert.False(layout.IsCustomized);
        Assert.Contains("could not be read", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSettings_ListTheSections_AndMoveAndHideEntries()
    {
        var layout = Create();
        using var libraries = Libraries();
        await libraries.RefreshAsync(TestContext.Current.CancellationToken);
        using var settings = new SidebarSettingsViewModel(layout, libraries);

        Assert.Equal(["Filme", "Serien"], settings.Groups[0].Rows.Select(row => row.Label));
        var calendar = settings.Groups[1].Rows.Single(row => row.Tag == "Calendar");
        var forYou = settings.Groups[1].Rows[0];
        Assert.False(forYou.MoveUpCommand.CanExecute(null));

        await calendar.MoveUpCommand.ExecuteAsync(null);
        settings.Groups[2].Rows.Single(row => row.Label == "History").IsShown = false;
        await layout.Saving;

        Assert.Equal(["For You", "Calendar", "Requests", "Notifications"], settings.Groups[1].Rows.Select(row => row.Label));
        Assert.Same(calendar, settings.Groups[1].Rows[1]);
        Assert.True(layout.IsHidden("List:history"));
        Assert.True(settings.IsCustomized);
        Assert.Equal(SidebarSettingsViewModel.ServerOfferNote, settings.Groups[1].Rows.Single(row => row.Tag == "Requests").Note);
    }

    [Fact]
    public async Task TheSettings_ShowTheLayoutWithoutChangingIt_AndResetShowsEverything()
    {
        var layout = Create();
        await layout.SetHiddenAsync("Collections", true);
        int revision = layout.Revision;
        using var libraries = Libraries();
        using var settings = new SidebarSettingsViewModel(layout, libraries);
        var collections = settings.Groups[2].Rows.Single(row => row.Tag == "Collections");

        Assert.False(collections.IsShown);
        Assert.Equal(revision, layout.Revision);
        Assert.False(settings.Groups[0].HasRows);

        await settings.ResetCommand.ExecuteAsync(null);

        Assert.True(collections.IsShown);
        Assert.False(settings.IsCustomized);
    }

    private SidebarViewModel Libraries()
    {
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/user/libraries", StringComparison.Ordinal)
            ? FakeSiloServer.Json(200, """{"items":[{"id":"1","name":"Filme","type":"movies","sort_order":1},{"id":"2","name":"Serien","type":"series","sort_order":2}]}""")
            : FakeSiloServer.Json(200, """{"items":[],"avatar_upload_enabled":false}""")));
        var http = new SiloHttp(new HttpClient(handler));
        _session.Account = new Account("1", "elena", "", "admin", [], true, false, null);
        var libraries = new VisibleLibraries(new CatalogApi(http), new SettingsApi(http), _logs.For<VisibleLibraries>());
        return new SidebarViewModel(_session, new ProfilesApi(http), libraries, new RecordingNavigation(), new ImmediateDispatcher(),
            _logs.For<SidebarViewModel>());
    }
}
