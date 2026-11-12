using System.Text.Json;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// Settings → Navigation &amp; Cards → Pages: the title preview at the top, on by default, and the hero's size, compact by
/// default; both kept on this PC for each server and profile.
/// </summary>
public sealed class BrowseHeroSettingTests : IDisposable
{
    private static readonly ServerEntry Server = new("srv1", new Uri("https://silo.test"), "Elena's Silo", null, default, default, null);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSessionManager _session = new();
    private readonly LogSink _logs = new();

    public BrowseHeroSettingTests()
    {
        _session.State = SessionState.Ready;
        _session.ActiveServer = Server;
        _session.ProfileId = "7";
    }

    private string FilePath => Path.Combine(_directory, "browse-hero.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private BrowseHeroSetting Create() => new(FilePath, _session, new ImmediateDispatcher(), _logs.For<BrowseHeroSetting>());

    [Fact]
    public void AtFirst_TheTitlePreviewShows_AndNothingIsWritten()
    {
        var setting = Create();

        Assert.True(setting.IsShown);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task TurnedOff_ItStaysOff_AtTheNextStart()
    {
        var setting = Create();

        await setting.SetShownAsync(false);

        Assert.False(setting.IsShown);
        Assert.False(Create().IsShown);
    }

    [Fact]
    public async Task TurnedOnAgain_ItShowsAgain_AtTheNextStart()
    {
        var setting = Create();
        await setting.SetShownAsync(false);

        await setting.SetShownAsync(true);

        Assert.True(setting.IsShown);
        Assert.True(Create().IsShown);
    }

    [Fact]
    public async Task TheSettingsSwitch_SavesWhatItIsSetTo()
    {
        var setting = Create();
        using var page = new TitlePreviewSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IsShown = false;
        await setting.Saving;

        Assert.False(page.IsShown);
        Assert.Equal([nameof(TitlePreviewSettingsViewModel.IsShown)], changed);
        Assert.False(Create().IsShown);
    }

    [Fact]
    public async Task TheSettingsSwitch_ShowsTheChoiceOfTheProfileActing()
    {
        var setting = Create();
        await setting.SetShownAsync(false);
        using var page = new TitlePreviewSettingsViewModel(setting);
        Assert.False(page.IsShown);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.True(page.IsShown);
        Assert.Equal([nameof(TitlePreviewSettingsViewModel.IsShown)], changed);
    }

    [Fact]
    public async Task TheSettingsSwitch_LetsGoOfTheSetting_AsThePageLeaves()
    {
        var setting = Create();
        var page = new TitlePreviewSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.Dispose();
        await setting.SetShownAsync(false);

        Assert.Empty(changed);
    }

    [Fact]
    public async Task TheFile_NamesTheServerAndTheProfile()
    {
        await Create().SetShownAsync(false);

        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(["srv1/7"], file.RootElement.GetProperty("hidden_for").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task EachProfile_HasItsOwnSwitch()
    {
        var setting = Create();
        await setting.SetShownAsync(false);
        var changed = new List<string?>();
        setting.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.True(setting.IsShown);
        Assert.Contains(nameof(BrowseHeroSetting.IsShown), changed);

        _session.ProfileId = "7";
        _session.RaiseChanged();

        Assert.False(setting.IsShown);
    }

    [Fact]
    public async Task WithoutAProfile_TheSwitchGoesBack_OnceItsChangeIsDone_AndNothingIsWritten()
    {
        _session.ProfileId = null;
        var dispatcher = new QueuedDispatcher();
        var setting = new BrowseHeroSetting(FilePath, _session, dispatcher, _logs.For<BrowseHeroSetting>());
        using var page = new TitlePreviewSettingsViewModel(setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IsShown = false;
        await setting.Saving;

        // Not from inside the switch's own change, which its binding would not take.
        Assert.Empty(changed);
        dispatcher.RunAll();
        Assert.True(page.IsShown);
        Assert.Equal([nameof(TitlePreviewSettingsViewModel.IsShown)], changed);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void AFileThatCannotBeRead_ShowsTheTitlePreview()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        Assert.True(Create().IsShown);
        Assert.Equal(BrowseHeroSize.Compact, Create().Size);
    }

    [Fact]
    public void AtFirst_TheHeroIsCompact_AndNothingIsWritten()
    {
        var setting = Create();

        Assert.Equal(BrowseHeroSize.Compact, setting.Size);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task SetToFull_TheHeroStaysFull_AtTheNextStart_AndCompactAgainOnceSetBack()
    {
        var setting = Create();

        await setting.SetSizeAsync(BrowseHeroSize.Full);

        Assert.Equal(BrowseHeroSize.Full, setting.Size);
        Assert.Equal(BrowseHeroSize.Full, Create().Size);

        await setting.SetSizeAsync(BrowseHeroSize.Compact);

        Assert.Equal(BrowseHeroSize.Compact, setting.Size);
        Assert.Equal(BrowseHeroSize.Compact, Create().Size);
    }

    [Fact]
    public async Task TheFile_NamesTheServerAndTheProfileOfAFullHero_BesideTheSwitch()
    {
        await Create().SetSizeAsync(BrowseHeroSize.Full);

        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(["srv1/7"], file.RootElement.GetProperty("full_for").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(file.RootElement.GetProperty("hidden_for").EnumerateArray());
    }

    [Fact]
    public void AFileFromBeforeTheSizeCouldBeChosen_MeansCompact_AndKeepsItsSwitch()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{ "hidden_for": [ "srv1/7" ] }""");

        var setting = Create();

        Assert.Equal(BrowseHeroSize.Compact, setting.Size);
        Assert.False(setting.IsShown);
    }

    [Theory]
    [InlineData("""{ "full_for": null }""")]
    [InlineData("""{ "full_for": [ null, "srv1/8" ] }""")]
    [InlineData("""{ "hidden_for": [], "full_for": [] }""")]
    public void AFileThatNamesNoFullHeroForTheProfile_MeansCompact(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);

        Assert.Equal(BrowseHeroSize.Compact, Create().Size);
    }

    [Fact]
    public async Task TheSizeAndTheSwitch_AreKeptSideBySide()
    {
        var setting = Create();
        await setting.SetShownAsync(false);
        await setting.SetSizeAsync(BrowseHeroSize.Full);

        var next = Create();
        Assert.False(next.IsShown);
        Assert.Equal(BrowseHeroSize.Full, next.Size);

        await next.SetShownAsync(true);

        var last = Create();
        Assert.True(last.IsShown);
        Assert.Equal(BrowseHeroSize.Full, last.Size);
    }

    [Fact]
    public async Task EachProfile_HasItsOwnSize()
    {
        var setting = Create();
        await setting.SetSizeAsync(BrowseHeroSize.Full);
        var changed = new List<string?>();
        setting.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.Equal(BrowseHeroSize.Compact, setting.Size);
        Assert.Contains(nameof(BrowseHeroSetting.Size), changed);

        _session.ProfileId = "7";
        _session.RaiseChanged();

        Assert.Equal(BrowseHeroSize.Full, setting.Size);
    }

    [Fact]
    public async Task WithoutAProfile_TheSizeStaysCompact_AndNothingIsWritten()
    {
        _session.ProfileId = null;
        var setting = Create();

        await setting.SetSizeAsync(BrowseHeroSize.Full);

        Assert.Equal(BrowseHeroSize.Compact, setting.Size);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void TheSettingsChoices_AreCompactAndFull_WithCompactChosenAtFirst()
    {
        using var page = new TitlePreviewSettingsViewModel(Create());

        Assert.Equal(["Compact", "Full"], page.Sizes.Select(choice => choice.Label));
        Assert.Equal("Compact", Assert.Single(page.Sizes, choice => choice.IsSelected).Label);
    }

    [Fact]
    public async Task PickingFull_SavesIt_AndShowsItChosen()
    {
        var setting = Create();
        using var page = new TitlePreviewSettingsViewModel(setting);
        var full = page.Sizes[1];

        await full.PickCommand.ExecuteAsync(null);
        await setting.Saving;

        Assert.Equal("Full", Assert.Single(page.Sizes, choice => choice.IsSelected).Label);
        // The same choices while the pick changes, so focus stays on the one picked.
        Assert.Same(full, page.Sizes[1]);
        Assert.Equal(BrowseHeroSize.Full, Create().Size);
    }

    [Fact]
    public async Task TheSettingsChoices_ShowTheSizeOfTheProfileActing()
    {
        var setting = Create();
        await setting.SetSizeAsync(BrowseHeroSize.Full);
        using var page = new TitlePreviewSettingsViewModel(setting);
        Assert.Equal("Full", Assert.Single(page.Sizes, choice => choice.IsSelected).Label);

        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.Equal("Compact", Assert.Single(page.Sizes, choice => choice.IsSelected).Label);
    }

    [Fact]
    public async Task TheSettingsChoices_LetGoOfTheSetting_AsThePageLeaves()
    {
        var setting = Create();
        var page = new TitlePreviewSettingsViewModel(setting);

        page.Dispose();
        await setting.SetSizeAsync(BrowseHeroSize.Full);

        Assert.Equal("Compact", Assert.Single(page.Sizes, choice => choice.IsSelected).Label);
    }
}
